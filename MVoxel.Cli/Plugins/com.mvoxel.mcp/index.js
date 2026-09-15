/**
 * MVoxel MCP 内置插件入口
 * CLI 负责提供 MCP stdio 协议和项目级工具
 */
export function activate(host) {
  host.registerAutomation("mvoxel.project", "通过 MCP 管理项目和插件");
}

export function runAutomation(id, payload) {
  if (id !== "mvoxel.project") throw new Error("未知的自动化入口");
  return { accepted: true, payload: payload ?? {} };
}
