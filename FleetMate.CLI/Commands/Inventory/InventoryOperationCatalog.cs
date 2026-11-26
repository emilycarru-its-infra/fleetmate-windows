namespace FleetMate.Commands.Inventory;

internal static class InventoryOperationCatalog
{
    // One operation name, one route per inventory backend.
    //
    //   group|operation|snipe route|cellarum route
    //
    // A route is METHOD path, relative to the backend's /api/v1 root, or `-` when
    // that backend has no counterpart. {id} is --id, {resource} is --resource and
    // {record} is --record translated to the backend's own name for that kind of
    // record. A path may carry a query string; --query adds to it.
    //
    // The macOS CLI carries this same table (Sources/FleetMate/Commands/Inventory/
    // InventoryOperationCatalog.swift in fleetmate-macintosh). Change both together.
    internal const string Operations = """
assets|list|GET hardware|GET items
assets|show|GET hardware/{id}|GET items/{id}
assets|find-tag|GET hardware/bytag/{id}|GET items/lookup?tag={id}
assets|find-serial|GET hardware/byserial/{id}|GET items/lookup?serial={id}
assets|history|GET hardware/{id}/history|GET items/{id}/events
assets|create|POST hardware|POST items
assets|update|PATCH hardware/{id}|PATCH items/{id}
assets|checkout|POST hardware/{id}/checkout|POST items/{id}/checkout
assets|checkin|POST hardware/{id}/checkin|POST items/{id}/checkin
assets|audit|POST hardware/{id}/audit|POST items/{id}/audits
assets|audits|-|GET items/{id}/audits
assets|licenses|GET hardware/{id}/licenses|-
assets|buyout-request|-|GET items/{id}/buyout-request
assets|lease|-|GET items/{id}/lease
buyouts|list|GET buyouts|GET lease-buyouts
buyouts|summary|-|GET lease-buyouts/summary
buyouts|show|GET buyouts/{id}|GET lease-buyouts/{id}
buyouts|create|POST buyouts|POST lease-buyouts
buyouts|update|PATCH buyouts/{id}|PATCH lease-buyouts/{id}
buyouts|transition|PATCH buyouts/{id}|POST lease-buyouts/{id}/transition
buyouts|quote|POST buyouts/{id}/quote|POST lease-buyouts/{id}/quotes
buyouts|agreement|POST buyouts/{id}/agreement|POST lease-buyouts/{id}/agreement
buyouts|payroll-notice|POST buyouts/{id}/payroll-notice|POST lease-buyouts/{id}/payroll-notice
buyouts|delete|DELETE buyouts/{id}|DELETE lease-buyouts/{id}
pickups|list|GET lease-pickups|GET lease-pickups
pickups|show|GET lease-pickups/{id}|GET lease-pickups/{id}
pickups|create|POST lease-pickups|POST lease-pickups
pickups|draft|-|POST lease-pickups/draft
pickups|schedule|POST lease-pickups/{id}/schedule|POST lease-pickups/{id}/schedule
pickups|picked-up|POST lease-pickups/{id}/picked-up|POST lease-pickups/{id}/picked-up
pickups|cancel|POST lease-pickups/{id}/cancel|POST lease-pickups/{id}/cancel
pickups|csv|-|GET lease-pickups/{id}/csv
lease|decisions|GET lease-decisions|GET lease-decisions
lease|schedules|-|GET lease-schedules
lease|contracts|-|GET lease-contracts
lease|cadence|GET procurement/lease-cadence|GET lease-schedules/cadence
lease|documents upload|POST lease-documents|POST lease-documents
deployments|decommission|GET deployments/decommission|-
deployments|board|-|GET deployment-board
deployments|forecast|GET deployments/forecast|GET deployment-forecast
deployments|types|GET deployments/types|GET deployment-types
deployments|type create|POST deployments/types|POST deployment-types
deployments|type update|PATCH deployments/types/{id}|PUT deployment-types/{id}
deployments|type delete|DELETE deployments/types/{id}|-
deployments|stages|GET deployments/stages|GET deployment-stages
deployments|stage show|GET deployments/stages/{id}|-
deployments|stage create|POST deployments/stages|POST deployment-stages
deployments|stage update|PATCH deployments/stages/{id}|PUT deployment-stages/{id}
deployments|stage delete|DELETE deployments/stages/{id}|-
deployments|waves|GET deployments/waves|GET deployment-waves
deployments|wave show|GET deployments/waves/{id}|GET deployment-waves/{id}
deployments|wave create|POST deployments/waves|POST deployment-waves
deployments|wave update|PATCH deployments/waves/{id}|PATCH deployment-waves/{id}
deployments|wave delete|DELETE deployments/waves/{id}|DELETE deployment-waves/{id}
deployments|wave announcement|GET deployments/waves/{id}/announce|GET deployment-waves/{id}/announcement
deployments|wave announce|POST deployments/waves/{id}/announce|POST deployment-waves/{id}/announcement
deployments|item add|POST deployments/waves/{id}/items|POST deployment-waves/{id}/items
deployments|item update|PATCH deployments/items/{id}|PATCH deployment-items/{id}
deployments|item delete|DELETE deployments/items/{id}|DELETE deployment-items/{id}
deployments|blackouts|GET deployments/blackouts|GET staff-blackouts
deployments|blackout create|POST deployments/blackouts|POST staff-blackouts
deployments|blackout update|PATCH deployments/blackouts/{id}|PATCH staff-blackouts/{id}
deployments|blackout delete|DELETE deployments/blackouts/{id}|DELETE staff-blackouts/{id}
agreements|list|GET user-agreements|GET user-agreements
agreements|show|GET user-agreements/{id}|GET user-agreements/{id}
agreements|create|POST user-agreements|POST user-agreements
agreements|update|PATCH user-agreements/{id}|PATCH user-agreements/{id}
agreements|send|POST user-agreements/{id}/send-for-signature|POST user-agreements/{id}/send
agreements|pickup-instructions|POST user-agreements/{id}/send-pickup-instructions|POST user-agreements/{id}/pickup-instructions
agreements|cancel|POST user-agreements/{id}/cancel|POST user-agreements/{id}/cancel
agreements|pdf|GET user-agreements/{id}/pdf|GET user-agreements/{id}/pdf
agreements|reconcile|POST user-agreements/reconcile|POST user-agreements/reconcile
orders|list|GET orders|GET orders
orders|show|GET orders/{id}|GET orders/{id}
orders|ingest|POST orders/ingest|POST orders/ingest
orders|receive|POST orders/{id}/receive|POST orders/{id}/receive
orders|send-vendor|POST orders/{id}/send-vendor|POST orders/{id}/vendor-send
orders|vendor-response|POST orders/{id}/vendor-response|POST orders/{id}/vendor-response
orders|vendor-order|PATCH orders/{id}/vendor-order|PATCH orders/{id}/vendor-order-number
orders|purchase-order|PATCH orders/{id}/purchase-order|-
orders|eta-request|POST orders/{id}/eta-request|POST orders/{id}/eta-request
orders|eta-preview|GET orders/{id}/eta-request|GET orders/{id}/eta-request
orders|item delete|DELETE orders/{id}/items/{resource}|DELETE orders/{id}/lines/{resource}
orders|shipment|-|POST orders/{id}/shipments
orders|invoices|GET order-invoices|GET invoices
orders|invoice show|-|GET invoices/{resource}
orders|invoice add|-|POST orders/{id}/invoices
orders|invoice approval|PATCH order-invoices/{resource}/approval|POST orders/{id}/invoices/{resource}/decision
orders|invoice payment|PATCH order-invoices/{resource}/payment|PUT invoices/{resource}/payment
purchase-orders|list|GET purchase-orders|GET purchase-orders
purchase-orders|show|GET purchase-orders/{id}|GET purchase-orders/{id}
purchase-orders|create|POST purchase-orders|POST purchase-orders
purchase-orders|provision|POST purchase-orders/{id}/provision|POST purchase-orders/{id}/provision
purchase-orders|raise|POST purchase-orders/{id}/orders|POST purchase-orders/{id}/orders
requisitions|list|GET requisitions|GET requisitions
requisitions|options|GET requisitions/options|GET requisitions/options
requisitions|catalog|GET requisitions/catalog|GET store/catalog
requisitions|show|GET requisitions/{id}|GET requisitions/{id}
requisitions|create|POST requisitions|POST requisitions
requisitions|promote|POST requisitions/{id}/promote|POST requisitions/{id}/promote
requisitions|delete|DELETE requisitions/{id}|DELETE requisitions/{id}
store|orders|GET store-orders|GET store/orders
store|order show|-|GET store/orders/{id}
store|order create|POST store-orders|POST store/orders
store|order revise|PATCH store-orders/{id}|POST store/approvals/{id}/revise
store|decide|POST store-orders/{id}/decide|POST store/approvals/{id}/decision
store|quote|POST store-orders/{id}/quote|POST store/approvals/{id}/quote
store|funding|POST store-orders/{id}/funding|PATCH store/approvals/{id}/funding
store|shipment|POST store-orders/{id}/shipment|POST store/orders/{id}/shipments
store|send-vendor|POST store-orders/send-vendor|POST store/vendor-send
store|approvers|GET procurement/approvers|-
catalog|list|GET catalog-items|GET catalog-items
catalog|show|-|GET catalog-items/{id}
catalog|create|POST catalog-items|POST catalog-items
catalog|update|-|PATCH catalog-items/{id}
catalog|delete|DELETE catalog-items/{id}|-
contracts|list|GET contracts|GET contracts
contracts|show|GET contracts/{id}|GET contracts/{id}
contracts|create|POST contracts|POST contracts
contracts|update|PATCH contracts/{id}|PATCH contracts/{id}
licenses|list|GET licenses|GET licenses
licenses|show|GET licenses/{id}|GET licenses/{id}
licenses|seats|GET licenses/{id}/seats|GET licenses/{id}/seats
licenses|history|GET licenses/{id}/history|GET licenses/{id}/history
maintenances|list|GET maintenances|GET maintenances
maintenances|show|GET maintenances/{id}|GET maintenances/{id}
maintenances|create|POST maintenances|POST maintenances
maintenances|update|PUT maintenances/{id}|PUT maintenances/{id}
people|list|GET users|GET people
people|show|GET users/{id}|GET people/{id}
people|assets|GET users/{id}/assets|GET people/{id}/events
people|me|GET users/me|GET me
locations|list|GET locations|GET locations
locations|show|GET locations/{id}|GET locations/{id}
locations|assets|GET locations/{id}/assets|GET locations/{id}/items
models|list|GET models|GET models
models|show|GET models/{id}|-
manufacturers|list|GET manufacturers|GET manufacturers
suppliers|list|GET suppliers|GET suppliers
suppliers|show|GET suppliers/{id}|GET suppliers/{id}
departments|list|GET departments|GET departments
statuses|list|GET statuslabels|GET statuses
categories|list|GET categories|GET classes
files|list|GET {record}/{id}/files|GET attachments?record_kind={record}&record_id={id}
files|download|GET {record}/{id}/files/{resource}|GET attachments/{resource}/content
files|delete|DELETE {record}/{id}/files/{resource}/delete|DELETE attachments/{resource}
email-templates|list|GET settings/emails|GET email-templates
email-templates|show|GET settings/emails/{id}|GET email-templates/{id}
email-templates|update|PATCH settings/emails/{id}|PUT email-templates/{id}
activity|list|GET reports/activity|GET events
""";

    // --record kinds, translated to each backend's path segment or record_kind.
    internal static readonly IReadOnlyDictionary<string, (string Snipe, string Cellarum)> RecordKinds =
        new Dictionary<string, (string Snipe, string Cellarum)>(StringComparer.Ordinal)
        {
            ["asset"] = ("hardware", "item"),
            ["person"] = ("users", "person"),
            ["license"] = ("licenses", "license"),
            ["model"] = ("models", "model"),
            ["supplier"] = ("suppliers", "supplier"),
            ["contract"] = ("contracts", "contract"),
            ["purchase-order"] = ("purchase-orders", "purchase_order"),
            ["lease-schedule"] = ("lease-schedules", "lease_schedule"),
            ["maintenance"] = ("maintenances", "maintenance"),
            ["location"] = ("locations", "-"),
        };
}
