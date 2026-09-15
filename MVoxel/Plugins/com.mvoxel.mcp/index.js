/**
 * MVoxel MCP 内置插件入口
 */
export function activate(host) {
  host.registerAutomation("mvoxel.project", "项目自动化入口");
  host.registerCommand("mvoxel.project.status", "读取项目状态");
  host.registerPanel("mvoxel.mcp", "MVoxel MCP");
}

export function runAutomation(id, payload) {
  if (id !== "mvoxel.project") throw new Error("未知的自动化入口");
  return { accepted: true, payload: payload ?? {} };
}

export function runCommand(id, payload) {
  if (id !== "mvoxel.project.status") throw new Error("未知的编辑器命令");
  return { accepted: true, payload: payload ?? {} };
}
