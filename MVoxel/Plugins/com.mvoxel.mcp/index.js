/**
 * MVoxel MCP 内置插件入口
 */
export function activate(host) {
  host.registerAutomation("mvoxel.project", "项目自动化入口");
}

export function runAutomation(id, payload) {
  if (id !== "mvoxel.project") throw new Error("未知的自动化入口");
  return { accepted: true, payload: payload ?? {} };
}
