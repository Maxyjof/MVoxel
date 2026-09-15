function activate(host)
  host.registerAutomation("mvoxel.lua.sample", "Lua 示例自动化")
  host.registerCommand("mvoxel.lua.sample.status", "读取 Lua 示例状态")
  host.registerCommand("mvoxel.lua.sample.timeout", "Lua 超时测试")
  host.registerPanel("mvoxel.lua.sample", "Lua 示例面板")
  host.registerComponent("mvoxel.lua.sample.marker", "Lua 示例组件", "示例")
end

function runAutomation(id, payload)
  return { accepted = id == "mvoxel.lua.sample", payload = payload }
end

function runCommand(id, payload)
  if id == "mvoxel.lua.sample.timeout" then while true do end end
  return { accepted = id == "mvoxel.lua.sample.status", payload = payload }
end
