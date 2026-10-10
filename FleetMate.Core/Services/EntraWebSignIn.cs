using System.Text.RegularExpressions;

namespace FleetMate.Core.Services;

/// <summary>
/// The pieces of a headless Entra web sign-in that do not depend on a browser:
/// the scripts injected into the hidden WebView2, the rule for which account
/// tile to pick, and how long a silent attempt may run.
///
/// FleetMate never shows a sign-in window. The browser that carries a web SSO
/// chain (TeamDynamix's Shibboleth → Entra, the Azure DevOps authorize page) is
/// created hidden, driven by these scripts, and torn down; when it cannot finish
/// on its own the system is reported as failed, never handed to the user.
/// </summary>
public static partial class EntraWebSignIn
{
    /// <summary>
    /// How long one headless attempt may run before it is reported as failed.
    /// The chain itself takes seconds; the allowance covers an Authenticator
    /// push the fallback script may request, which waits on a phone, and matches
    /// the macOS client.
    /// </summary>
    public static readonly TimeSpan HeadlessTimeout = TimeSpan.FromSeconds(95);

    /// <summary>Hosts that serve Entra's sign-in pages.</summary>
    private static readonly string[] EntraHosts =
    {
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.windows.net",
    };

    /// <summary>True when <paramref name="url"/> is an Entra sign-in page.</summary>
    public static bool IsEntraPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && EntraHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"[a-z0-9._%+\-]+@[a-z0-9.\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    /// <summary>
    /// Whether an account-picker tile belongs to <paramref name="upn"/>. The
    /// address must appear as a whole address in the tile, never as a substring:
    /// a second signed-in account such as <c>aws-adoe@example.edu</c> contains
    /// <c>adoe@example.edu</c>, and a substring match picks it, signs in as the
    /// wrong identity, and every API call comes back 403. This is the rule the
    /// injected picker script applies; it lives here too so it can be tested.
    /// </summary>
    public static bool MatchesAccountTile(string? tileText, string? upn)
    {
        if (string.IsNullOrWhiteSpace(tileText) || string.IsNullOrWhiteSpace(upn)) return false;
        var wanted = upn.Trim();
        return EmailPattern().Matches(tileText)
            .Any(m => m.Value.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Escape a value for a single-quoted JavaScript string literal.</summary>
    public static string EscapeForScript(string value) => value
        .Replace("\\", "\\\\")
        .Replace("'", "\\'")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("<", "\\x3c");

    /// <summary>
    /// Answers Entra's account picker with the tile whose address is exactly
    /// <paramref name="upn"/>, or, where Entra asks for a username instead, fills
    /// it in and presses Next. The hidden browser's profile keeps live Entra
    /// sessions, and with more than one signed-in account Entra asks which one
    /// to use; unanswered, that page is where a headless attempt used to stall.
    ///
    /// Progress is reported to the host through <c>chrome.webview.postMessage</c>
    /// as <c>{debug: …}</c> lines (console output from a hidden view goes
    /// nowhere) and <c>{account: 'picked'|'filled'}</c> once it has acted.
    /// </summary>
    public static string AccountScript(string upn)
    {
        var wanted = EscapeForScript(upn.Trim().ToLowerInvariant());
        return $$"""
        (function() {
            if (window.__fleetmateAccount) return;
            var acted = false;
            function post(m) { try { window.chrome.webview.postMessage(m); } catch (e) {} }
            function act() {
                if (acted) return;
                var wanted = '{{wanted}}';
                var tiles = document.querySelectorAll('[role="button"], [role="link"], .table, div[data-test-id], small');
                for (var i = 0; i < tiles.length; i++) {
                    var txt = (tiles[i].textContent || '').toLowerCase();
                    var emails = txt.match(/[a-z0-9._%+\-]+@[a-z0-9.\-]+/g) || [];
                    if (emails.indexOf(wanted) !== -1) {
                        var target = tiles[i].closest('[role="button"], [role="link"], .table') || tiles[i];
                        acted = true; window.__fleetmateAccount = true;
                        post({ debug: '[PICKER] Choosing signed-in account tile ' + emails.join(',') });
                        post({ account: 'picked' });
                        target.click();
                        return;
                    }
                }
                var input = document.querySelector('input[name="loginfmt"]:not([type="hidden"])')
                         || document.querySelector('input[type="email"]');
                if (!input || input.offsetParent === null) return;
                var setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                setter.call(input, wanted);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                acted = true; window.__fleetmateAccount = true;
                post({ debug: '[USERNAME] Filled the sign-in address' });
                post({ account: 'filled' });
                setTimeout(function() {
                    var next = document.querySelector('#idSIButton9')
                            || document.querySelector('input[type="submit"]')
                            || document.querySelector('button[type="submit"]');
                    if (next) next.click();
                }, 300);
            }
            act();
            [200, 500, 1000, 2000, 4000, 8000].forEach(function(d) { setTimeout(act, d); });
            if (document.body) {
                var observer = new MutationObserver(act);
                observer.observe(document.body, { childList: true, subtree: true });
                setTimeout(function() { observer.disconnect(); }, 15000);
            }
        })();
        """;
    }

    /// <summary>
    /// Accepts Entra's "Stay signed in?" prompt. Accepting turns the Entra
    /// session cookie into a persistent one the hidden browser's profile keeps,
    /// so the next launch completes without reaching a sign-in form at all.
    /// Entra reuses <c>#idSIButton9</c> for Next, Sign in and Yes, so it only
    /// clicks once it is sure the page is the KMSI prompt.
    /// </summary>
    public const string KmsiScript = """
        (function() {
            if (window.__fleetmateKmsi) return 'already-handled';
            var checkbox = document.querySelector('#KmsiCheckboxField');
            var bodyText = document.body ? document.body.innerText : '';
            var isKmsi = window.location.href.indexOf('kmsi') !== -1
                         || checkbox !== null
                         || bodyText.indexOf('Stay signed in') !== -1;
            if (!isKmsi) return 'not-kmsi';
            var yes = document.querySelector('#idSIButton9');
            if (!yes) return 'no-button';
            if (checkbox && !checkbox.checked) checkbox.click();
            window.__fleetmateKmsi = true;
            yes.click();
            return 'accepted';
        })();
        """;

    /// <summary>
    /// How long a hidden sign-in may sit on Entra's passkey page before it is
    /// given up. The method fallback has had its chance by then; a passkey
    /// needs a person at the machine, so waiting out the full
    /// <see cref="HeadlessTimeout"/> there only delays the same failure.
    /// </summary>
    public static readonly TimeSpan PasskeyGiveUp = TimeSpan.FromSeconds(25);

    /// <summary>True for Entra's passkey (FIDO) ceremony, e.g. <c>login.microsoft.com/{tenant}/fido/get</c>.</summary>
    public static bool IsPasskeyPage(string? url) =>
        IsEntraPage(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.Contains("/fido/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The failure reported when a hidden sign-in is left on the passkey page.</summary>
    public static string PasskeyReason(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"Entra asked for a passkey ({uri.Host}{uri.AbsolutePath}), which a hidden sign-in cannot answer"
            : "Entra asked for a passkey, which a hidden sign-in cannot answer";

    /// <summary>
    /// Moves Entra off a passkey page: "Sign in another way", then a non-FIDO
    /// method such as an Authenticator push. Never "Try again", which loops on
    /// the failing passkey.
    /// </summary>
    public const string MethodFallbackScript = """
        (function() {
            if (window.__fleetmateFidoFallback) return;
            window.__fleetmateFidoFallback = true;
            var acted = false;
            function post(m) { try { window.chrome.webview.postMessage({ debug: m }); } catch (e) {} }
            function tryFallback() {
                if (acted) return true;
                var elems = document.querySelectorAll('a, button, [role="link"], [role="button"], input[type="submit"], span[tabindex], div[tabindex], li[tabindex]');
                for (var i = 0; i < elems.length; i++) {
                    var text = (elems[i].textContent || elems[i].value || '').trim().toLowerCase();
                    if (text.indexOf('sign in another way') !== -1 ||
                        text.indexOf('other ways to sign in') !== -1 ||
                        text.indexOf('use another method') !== -1 ||
                        text.indexOf('use a different method') !== -1 ||
                        text.indexOf('try another way') !== -1 ||
                        text.indexOf("i can't use") !== -1) {
                        post('[FIDO] Clicking: ' + text);
                        elems[i].click();
                        acted = true;
                        return true;
                    }
                }
                var bodyText = (document.body && document.body.innerText) || '';
                var isFidoPage = bodyText.indexOf('passkey') !== -1 ||
                                 bodyText.indexOf('security key') !== -1 ||
                                 bodyText.indexOf('FIDO') !== -1;
                var isErrorPage = bodyText.indexOf("couldn’t sign you in") !== -1 ||
                                  bodyText.indexOf("couldn't sign you in") !== -1 ||
                                  bodyText.indexOf('Something went wrong') !== -1;
                if (!isFidoPage && !isErrorPage) return false;
                var tiles = document.querySelectorAll('[data-value]');
                var preferred = ['PhoneAppNotification', 'PhoneAppOTP', 'OneWaySMS', 'TwoWayVoiceMobile'];
                for (var p = 0; p < preferred.length; p++) {
                    for (var t = 0; t < tiles.length; t++) {
                        if ((tiles[t].getAttribute('data-value') || '') === preferred[p]) {
                            post('[FIDO] Selecting method: ' + preferred[p]);
                            tiles[t].click();
                            acted = true;
                            return true;
                        }
                    }
                }
                post('[FIDO] Page: ' + location.host + location.pathname + ' Body: ' + bodyText.slice(0, 160).replace(/\s+/g, ' '));
                return false;
            }
            if (document.body) {
                var observer = new MutationObserver(function() { tryFallback(); });
                observer.observe(document.body, { childList: true, subtree: true });
                setTimeout(function() { observer.disconnect(); }, 45000);
            }
            [0, 500, 1000, 2000, 4000, 8000, 13000, 20000].forEach(function(d) { setTimeout(tryFallback, d); });
        })();
        """;

    /// <summary>
    /// Makes WebAuthn unavailable in a hidden sign-in browser. Run before any
    /// page script (<c>AddScriptToExecuteOnDocumentCreatedAsync</c>), in every
    /// frame. Without it Entra's passkey page calls <c>navigator.credentials.get</c>,
    /// and Windows answers that with its own visible "Windows Security" passkey
    /// dialog, outside the hidden browser — a window the user never asked for.
    /// Here the call is refused at once with <c>NotAllowedError</c>, which is
    /// what a cancelled passkey prompt returns, so Entra offers another method or
    /// the attempt fails on its passkey page with that reason.
    /// </summary>
    public const string WebAuthnBlockScript = """
        (function() {
            function post(m) { try { window.chrome.webview.postMessage(m); } catch (e) {} }
            function refuse(kind) {
                return function() {
                    post({ debug: '[WEBAUTHN] Refused navigator.credentials.' + kind + ' in the hidden browser', webauthn: kind });
                    return Promise.reject(new DOMException('Passkeys are not available in a hidden sign-in', 'NotAllowedError'));
                };
            }
            function no() { return Promise.resolve(false); }
            try {
                if (window.CredentialsContainer) {
                    Object.defineProperty(CredentialsContainer.prototype, 'get', { value: refuse('get'), configurable: false, writable: false });
                    Object.defineProperty(CredentialsContainer.prototype, 'create', { value: refuse('create'), configurable: false, writable: false });
                }
            } catch (e) {}
            try {
                if (window.PublicKeyCredential) {
                    Object.defineProperty(PublicKeyCredential, 'isUserVerifyingPlatformAuthenticatorAvailable', { value: no, configurable: false, writable: false });
                    Object.defineProperty(PublicKeyCredential, 'isConditionalMediationAvailable', { value: no, configurable: false, writable: false });
                }
            } catch (e) {}
        })();
        """;
}
