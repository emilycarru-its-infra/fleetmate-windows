namespace FleetMate.Commands.Projects;

internal static class DevOpsOperationCatalog
{
    // Method, REST path, API version and optional request scope for each CLI operation.
    internal const string Operations = """
repos|list|GET|git/repositories|7.1
repos|show|GET|git/repositories/{repository}|7.1
repos|create|POST|git/repositories|7.1
repos|update|PATCH|git/repositories/{repository}|7.1
repos|delete|DELETE|git/repositories/{repository}|7.1
repos|ref list|GET|git/repositories/{repository}/refs|7.1
repos|ref update|POST|git/repositories/{repository}/refs|7.1
repos|ref create|POST|git/repositories/{repository}/refs|7.1
repos|ref delete|POST|git/repositories/{repository}/refs|7.1
repos|ref lock|PATCH|git/repositories/{repository}/refs|7.1
repos|ref unlock|PATCH|git/repositories/{repository}/refs|7.1
repos|policy list|GET|policy/configurations|7.1
repos|policy show|GET|policy/configurations/{id}|7.1
repos|policy create|POST|policy/configurations|7.1
repos|policy update|PUT|policy/configurations/{id}|7.1
repos|policy delete|DELETE|policy/configurations/{id}|7.1
repos|policy type list|GET|policy/types|7.1
repos|pr list|GET|git/repositories/{repository}/pullrequests|7.1
repos|pr show|GET|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr create|POST|git/repositories/{repository}/pullrequests|7.1
repos|pr update|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr auto-complete|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr abandon|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr reopen|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr ready|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr draft|PATCH|git/repositories/{repository}/pullrequests/{id}|7.1
repos|pr reviewer list|GET|git/repositories/{repository}/pullrequests/{id}/reviewers|7.1
repos|pr reviewer add|PUT|git/repositories/{repository}/pullrequests/{id}/reviewers/{resource}|7.1
repos|pr reviewer remove|DELETE|git/repositories/{repository}/pullrequests/{id}/reviewers/{resource}|7.1
repos|pr work-item list|GET|git/repositories/{repository}/pullrequests/{id}/workitems|7.1
repos|pr thread list|GET|git/repositories/{repository}/pullrequests/{id}/threads|7.1
repos|pr commit list|GET|git/repositories/{repository}/pullrequests/{id}/commits|7.1
repos|import list|GET|git/repositories/{repository}/importRequests|7.1
repos|import show|GET|git/repositories/{repository}/importRequests/{id}|7.1
repos|import create|POST|git/repositories/{repository}/importRequests|7.1
pipelines|list|GET|pipelines|7.1
pipelines|show|GET|pipelines/{id}|7.1
pipelines|create|POST|pipelines|7.1
pipelines|update|PUT|build/definitions/{id}|7.1
pipelines|delete|DELETE|build/definitions/{id}|7.1
pipelines|run|POST|pipelines/{id}/runs|7.1
pipelines|runs list|GET|pipelines/{id}/runs|7.1
pipelines|runs show|GET|pipelines/{id}/runs/{resource}|7.1
pipelines|runs cancel|PATCH|build/builds/{id}|7.1
pipelines|runs tag list|GET|build/builds/{id}/tags|7.1
pipelines|runs tag add|PUT|build/builds/{id}/tags/{resource}|7.1
pipelines|runs tag delete|DELETE|build/builds/{id}/tags/{resource}|7.1
pipelines|build list|GET|build/builds|7.1
pipelines|build show|GET|build/builds/{id}|7.1
pipelines|build queue|POST|build/builds|7.1
pipelines|build cancel|PATCH|build/builds/{id}|7.1
pipelines|build delete|DELETE|build/builds/{id}|7.1
pipelines|build log list|GET|build/builds/{id}/logs|7.1
pipelines|build log show|GET|build/builds/{id}/logs/{resource}|7.1
pipelines|build artifact list|GET|build/builds/{id}/artifacts|7.1
pipelines|build artifact show|GET|build/builds/{id}/artifacts|7.1
pipelines|definition list|GET|build/definitions|7.1
pipelines|definition show|GET|build/definitions/{id}|7.1
pipelines|definition create|POST|build/definitions|7.1
pipelines|definition update|PUT|build/definitions/{id}|7.1
pipelines|definition delete|DELETE|build/definitions/{id}|7.1
pipelines|approval list|GET|pipelines/approvals|7.1
pipelines|approval show|GET|pipelines/approvals/{resource}|7.1
pipelines|approval update|PATCH|pipelines/approvals|7.1
pipelines|approval approve|PATCH|pipelines/approvals|7.1
pipelines|approval reject|PATCH|pipelines/approvals|7.1
pipelines|check list|GET|pipelines/checks/configurations|7.1-preview.1
pipelines|check show|GET|pipelines/checks/configurations/{id}|7.1-preview.1
pipelines|check create|POST|pipelines/checks/configurations|7.1-preview.1
pipelines|check update|PATCH|pipelines/checks/configurations/{id}|7.1-preview.1
pipelines|check delete|DELETE|pipelines/checks/configurations/{id}|7.1-preview.1
pipelines|pool list|GET|distributedtask/pools|7.1|org
pipelines|pool show|GET|distributedtask/pools/{id}|7.1|org
pipelines|agent list|GET|distributedtask/pools/{id}/agents|7.1|org
pipelines|agent show|GET|distributedtask/pools/{id}/agents/{resource}|7.1|org
pipelines|queue list|GET|distributedtask/queues|7.1
pipelines|queue show|GET|distributedtask/queues/{id}|7.1
pipelines|variable-group list|GET|distributedtask/variablegroups|7.1-preview.2
pipelines|variable-group show|GET|distributedtask/variablegroups/{id}|7.1-preview.2
pipelines|variable-group create|POST|distributedtask/variablegroups|7.1-preview.2|org
pipelines|variable-group update|PUT|distributedtask/variablegroups/{id}|7.1-preview.2|org
pipelines|variable-group delete|DELETE|distributedtask/variablegroups/{id}|7.1-preview.2|org
pipelines|release list|GET|release/releases|7.1|release
pipelines|release show|GET|release/releases/{id}|7.1|release
pipelines|release create|POST|release/releases|7.1|release
pipelines|release update|PUT|release/releases/{id}|7.1|release
pipelines|release delete|DELETE|release/releases/{id}|7.1|release
pipelines|release-definition list|GET|release/definitions|7.1|release
pipelines|release-definition show|GET|release/definitions/{id}|7.1|release
pipelines|release-definition create|POST|release/definitions|7.1|release
pipelines|release-definition update|PUT|release/definitions|7.1|release
pipelines|release-definition delete|DELETE|release/definitions/{id}|7.1|release
""";
}
