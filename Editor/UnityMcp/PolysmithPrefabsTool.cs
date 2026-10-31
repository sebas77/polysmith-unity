using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Polysmith
{
    [McpForUnityTool("polysmith_prefabs", Description =
        "Prefab assets only. action=find returns editable prefab paths using Unity's asset filter. " +
        "action=open selects explicit prefab_paths and opens one MCP-enabled Polysmith per prefab using the existing plugin. " +
        "Never exports or replaces meshes. Inspect results in Polysmith MCP and export ONLY on user request. " +
        "Match each returned process id to polysmith_sessions. Partial failures are reported per prefab; do not relaunch successes.")]
    public static class PolysmithPrefabsTool
    {
        public sealed class Parameters
        {
            [ToolParameter("find or open")]
            public string action { get; set; }

            [ToolParameter("Unity asset filter, e.g. Tree or l:Vegetation (find only).", Required = false)]
            public string filter { get; set; }

            [ToolParameter("Exact Assets/.../*.prefab paths (open only).", Required = false)]
            public string[] prefab_paths { get; set; }

            [ToolParameter("Absolute automation-capable Polysmith.exe; omitted uses the menu's saved path.", Required = false)]
            public string executable { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            try
            {
                var args = parameters?.ToObject<Parameters>();
                if (args == null)
                    return new ErrorResponse("Parameters are required.");
                if (args.action == "find")
                    return new SuccessResponse("Editable prefab assets; inspect paths before opening.",
                        new { prefabs = PolysmithAutomation.FindPrefabs(args.filter ?? "") });
                if (args.action == "open")
                {
                    var results = PolysmithAutomation.OpenPrefabs(args.prefab_paths, args.executable);
                    bool allOpened = Array.TrueForAll(results, result => result.Opened);
                    return new SuccessResponse(allOpened ? "Prefabs opened; no meshes exported or replaced." :
                        "Some prefabs could not open; inspect each result before retrying.", new { allOpened, results });
                }
                return new ErrorResponse("action must be find or open. Scene objects are unsupported.");
            }
            catch (Exception error)
            {
                return new ErrorResponse(error.Message);
            }
        }
    }
}
