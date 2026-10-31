using System.Text;
using AgentBridge.Contracts;

namespace AgentBridge.Capabilities;

/// <summary>
/// README.md for the cloud agent. It has never seen this system, so it explains the whole protocol;
/// the capabilities themselves are described in catalog.json.
/// </summary>
internal static class ReadmeRenderer
{
    public static string Render(Catalog catalog)
    {
        var readme = $$"""
            # AgentBridge: your local agent

            This folder connects you to **{{catalog.Agent.Id}}**, a small program that runs on the user's own Windows laptop.
            It can do a fixed list of named things (its *capabilities*) that need data or tools which exist only on that machine,
            and it hands the results back to you as files in this folder. You never connect to the machine directly:
            you write a request file, the agent picks it up, runs the capability and writes a response file.

            - Agent id: `{{catalog.Agent.Id}}`
            - Current catalogVersion: `{{catalog.CatalogVersion}}`
            - What you may ask for, with the exact arguments: `catalog.json` in this folder. Read it before you send a request.
              It is the only source of truth for capability names, targets and arguments. This file explains the protocol.

            ## Sending a request

            1. Read `catalog.json` and pick a capability. Its `description` says what it returns and `useWhen` says when to choose it.
               Never invent a capability name, a target name or an argument.
            2. Choose a new `requestId`: 1 to 128 characters of letters, digits, `.`, `_` and `-`, starting and ending with a letter or digit.
               Use a new GUID. A requestId must be unique: never reuse one, not even to retry.
            3. Write ONE new file `inbox/{requestId}.json` that contains the request (fields below). One file per request.
            4. Never edit or overwrite a file after you wrote it. To ask for something different, send a new request with a new requestId.

            Never put commands, file paths or credentials in a request. A request only names a capability and its arguments;
            the agent decides what runs. A `target` is a name from `catalog.json`, never a path.

            ### Request fields

            | Field | Required | Meaning |
            |---|---|---|
            | `schemaVersion` | yes | Always `1`. |
            | `requestId` | yes | The unique id described above. Use it as the file name too: `inbox/{requestId}.json`. |
            | `capability` | yes | A capability `name` from `catalog.json`, for example `agent.ping`. |
            | `target` | only if the capability has `requiresTarget: true` | A target `name` from the `targets` list in `catalog.json`. |
            | `args` | depends on the capability | A JSON object with the arguments listed under `args` of that capability in `catalog.json`. Leave it out or use `{ }` when there are none. |
            | `expiresAtUtc` | no | UTC time (for example `2026-11-01T09:00:00Z`) after which the request must not be started. Use it when a late answer is useless. |
            | `purpose` | no | One sentence on why you ask. It is only logged; it never changes what runs. |
            | `correlationId` | no | Any short text that groups the requests of one analysis. It is copied into the response. |

            ### Example

            Saved as `inbox/3f2b8c1e-7a4d-4e0b-9c55-2d6f0a1b9e47.json`. Do not copy this requestId; create your own.

            ```json
            {
              "schemaVersion": 1,
              "requestId": "3f2b8c1e-7a4d-4e0b-9c55-2d6f0a1b9e47",
              "capability": "agent.ping",
              "args": { "echo": "hello" },
              "purpose": "Check that the agent is reachable before starting the analysis"
            }
            ```

            `catalog.json` contains a ready-made `example` request for every capability.

            ## Reading the result

            The response appears as `responses/{requestId}.json`, with the same requestId as the request.
            Box sync can take from a few seconds to several minutes, so the file may not be there yet.
            If it is missing, wait and check again later. Do not send the request again with the same requestId.
            If nothing arrives after a long wait, tell the user: the laptop may be asleep or the agent may not be running.

            A response contains: `status`, `summary` (one line), `result` (the capability's data; its format is the capability's `output`
            in `catalog.json`), `artifacts`, `error` (`code` and `message`, present for every status except `succeeded`),
            `catalogVersion`, `agent`, `startedAtUtc`, `completedAtUtc` and the `correlationId` you sent.

            ### Statuses

            | Status | Meaning |
            |---|---|
            | `succeeded` | The capability ran. Read `result` and `artifacts`. |
            | `failed` | The capability ran but did not produce a result. `error` says why. |
            | `rejected` | The request was not run because it is invalid. `error` says what to fix. |
            | `timedOut` | The capability did not finish within its `timeoutSeconds` and was stopped. |
            | `interrupted` | The agent stopped while it was processing the request. It may have done part of the work and is never run again automatically. |

            These five statuses are final. Any other status, or no response file yet, means the request is **not finished yet**:
            wait and read the file again later.

            ### Error codes

            | `error.code` | What to do |
            |---|---|
            | `{{ErrorCodes.InvalidRequest}}` | The request file is malformed or a field is wrong; the message names the field. Fix it and send a new request with a new requestId. |
            | `{{ErrorCodes.UnsupportedSchemaVersion}}` | Use `"schemaVersion": 1`. |
            | `{{ErrorCodes.UnknownCapability}}` | The capability is not in the catalog. Re-read `catalog.json`, choose a listed capability and send a new request. |
            | `{{ErrorCodes.TargetRequired}}` | This capability needs a `target`. Add one from the `targets` list in `catalog.json` and send a new request. |
            | `{{ErrorCodes.UnknownTarget}}` | The target is not in the catalog. Use a name from the `targets` list in `catalog.json`. |
            | `{{ErrorCodes.InvalidArgs}}` | `args` does not match what the capability accepts; the message lists what is accepted. Re-read `catalog.json`, fix `args` and send a new request. |
            | `{{ErrorCodes.Expired}}` | `expiresAtUtc` had passed before the agent could start the request, usually because of slow sync. Send it again with a later `expiresAtUtc` (or none) and a new requestId. |
            | `{{ErrorCodes.DuplicateConflict}}` | A request with this requestId but different content was already received. The first request is not affected. Use a new requestId for the new request. |
            | `{{ErrorCodes.Timeout}}` | The capability needed more time than allowed. Try a narrower request (smaller scope, fewer items) or tell the user. |
            | `{{ErrorCodes.ExecutionFailed}}` | The capability ran but failed; the message says why. Do not retry blindly: change what the message points to, or tell the user. |
            | `{{ErrorCodes.ArtifactTooLarge}}` | The result was bigger than this agent may publish, so nothing was published. Ask for less (narrower scope, shorter period) with a new requestId. |
            | `{{ErrorCodes.Interrupted}}` | The agent was stopped while running the request. If you still need the result, send a new request with a new requestId. |
            | `{{ErrorCodes.InternalError}}` | An unexpected fault in the agent. Try once more with a new requestId; if it happens again, tell the user. |

            ### When the catalog changes

            Every response carries `catalogVersion`. If it differs from the `catalogVersion` of the `catalog.json` you used to build the request,
            the agent's configuration has changed (capabilities or arguments added, removed or changed).
            Re-read `catalog.json` and this file before you send more requests.

            ### Files (artifacts)

            Some capabilities return files, for example a Markdown export. They are listed in `artifacts` of the response as
            `{ "path", "contentType", "sizeBytes" }`. `path` is relative to the root of this folder and always starts with `artifacts/`.
            Open the file from there. Like responses, a new file can take a moment to sync.

            ## Capabilities right now

            {{RenderCapabilities(catalog)}}

            Arguments, targets, limits and a ready-made example request for each of them are in `catalog.json`.
            """;

        return readme.ReplaceLineEndings("\n");
    }

    private static string RenderCapabilities(Catalog catalog)
    {
        if (catalog.Capabilities.Count == 0)
        {
            return "_No capabilities are enabled on this agent right now._";
        }

        var list = new StringBuilder();
        foreach (var capability in catalog.Capabilities)
        {
            if (list.Length > 0)
            {
                list.Append('\n');
            }
            list.Append("- `").Append(capability.Name).Append("` — ").Append(OneLine(capability.Description));
            if (capability.RequiresTarget)
            {
                list.Append(" (needs a `target`)");
            }
        }
        return list.ToString();
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
