Sharpwire — sample folder plugins
================================

ExampleToolsPlugin.cs is a minimal plugin the app ships beside the executable under:

  PluginSamples/ExampleToolsPlugin.cs

On first run, if your workspace "plugins" folder has no .cs files yet, Sharpwire copies this file there so agents can call:

  GetUtcTimeIso, Echo, AddIntegers, WaitAndGreet

To use manually: copy ExampleToolsPlugin.cs into:

  <workspace>/plugins/

Then click Reload (toolbar) or restart the app.

Edit the .cs file, reload again to recompile. Remove it from plugins/ if you do not want these tools.

Dependency DLLs:
  • If a plugin needs third-party assemblies, place them in a sibling `lib` folder beside the plugin source.
  • Example:
      <workspace>/plugins/MyPlugin/MyPlugin.cs
      <workspace>/plugins/MyPlugin/lib/SomeDependency.dll
  • Reload plugins after updating either .cs files or lib/*.dll files.

Rules for folder plugins:
  • Public class with a parameterless constructor (implicit ok if you declare none).
  • Each tool: public method with [System.ComponentModel.Description] on the method.
  • Optional [Description] on parameters for the model.
  • Sharpwire.Core.MetaToolbox is allowed for settings attributes.
  • Sharpwire.Core.Hooks is allowed for lifecycle middleware.

Optional settings UI (host discovers these via reflection after compile):
  • Declare a class with [Sharpwire.Core.MetaToolbox.PluginSettings("My Plugin")] (the name shown in the app).
  • Mark properties with [Sharpwire.Core.MetaToolbox.PluginSetting("Label", "Description", isSecret: true)].
  • Editors: string and int use a text box; bool uses a toggle; isSecret uses a masked text box.
  • Values persist under "pluginSettings" in .sharpwire/settings.json.
  • After reload, open Settings → LOADED PLUGINS → Edit to open a tab for that plugin.
  • Folder plugins normally avoid Sharpwire.* for tools only; settings attributes are an exception — the host references include Sharpwire so these attribute types resolve at plugin compile time.
  • Optional: implement Sharpwire.Core.MetaToolbox.IPluginWithSettings on the same class as your tools so OnSettingsLoaded runs after compile and whenever settings are saved (same instance as the LLM calls).

Lifecycle hooks (mutable middleware):
  • Implement Sharpwire.Core.Hooks.ILifecycleHookMiddleware.
  • Hook context types: OrchestratorTurnHookContext, AgentExecutionHookContext, AgentStepHookContext, ChainHandoffHookContext, ToolApprovalHookContext.
  • Hook stages: OrchestratorTurnStart/End, AgentExecutionStart/End, AgentStepStart/End, ChainHandoff, ToolApprovalRequest/Result.
  • Tool approval context is ToolApprovalHookContext with ToolId, Details, and Approved (no Arguments property).
  • Plugins may post concise system chat notes via Sharpwire.Core.Hooks.PluginChat.TryPostSystem("message", "PluginName") (host rate-limited).
  • You may mutate fields like Task, ResponseText, HandoffTask, or approval Details before runtime uses them.
  • To block an action, call context.Block("reason") and return without calling next.
  • See HookLogger.cs for a complete sample that posts one chat line per hook event.
