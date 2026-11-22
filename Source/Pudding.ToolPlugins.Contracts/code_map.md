# Plugin Contracts
> 源指纹: *.cs=3-701edeb6e7a7, *.csproj=1-e6530e1caf4b · 条目数: 0 · 最近整理: 2026-11-09

  File   Responsibility  
 --- --- 
  `PluginContracts.cs`   Package, tool, advisory hook, scope, result and worker ports; BCL-only authoring boundary  
  `PluginAgentApi.cs`   Optional Core API 1.1 transport and host-issued invocation lease ports; no public SDK or host dependency  
  `PluginOperatorApi.cs`   Core API 1.2 operator grant and separate invocation lease factory; no mutable registry or model access  
  `Pudding.ToolPlugins.Contracts.csproj`   Compile-time prohibition on project references  
