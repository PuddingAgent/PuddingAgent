using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.PowerShell;

namespace PuddingCodeIntelligenceTests.PowerShell;

[TestClass]
public class PowerShellFileOutlinerTests
{
    private readonly PowerShellFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsPs()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".ps1"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".psm1"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".psd1"));
    }

    [TestMethod]
    public async Task OutlineAsync_FunctionDeclaration_Extracted()
    {
        var source = """
            function Get-UserInfo {
                param(
                    [string]$UserName,
                    [int]$UserId
                )
                Write-Host "Getting user $UserName"
            }
            """;

        var result = await _outliner.OutlineAsync("test.ps1", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 1);
        Assert.AreEqual("Get-UserInfo", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Method, result.Nodes[0].Kind);
    }

    [TestMethod]
    public async Task OutlineAsync_MultipleFunctions_AllExtracted()
    {
        var source = """
            function Start-Server {
                param([string]$Port)
                Write-Host "Starting on port $Port"
            }

            function Stop-Server {
                Write-Host "Stopping server"
            }

            function Restart-Server {
                Stop-Server
                Start-Server -Port 8080
            }
            """;

        var result = await _outliner.OutlineAsync("server.ps1", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Nodes.Count);
        Assert.AreEqual("Start-Server", result.Nodes[0].Name);
        Assert.AreEqual("Stop-Server", result.Nodes[1].Name);
        Assert.AreEqual("Restart-Server", result.Nodes[2].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_Class_Extracted()
    {
        var source = """
            class ServerConfig {
                [string]$HostName
                [int]$Port

                [void] Start() {
                    Write-Host "Starting"
                }
            }
            """;

        var result = await _outliner.OutlineAsync("config.ps1", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "ServerConfig" && n.Kind == CodeSymbolKind.Class));
    }

    [TestMethod]
    public async Task OutlineAsync_Enum_Extracted()
    {
        var source = """
            enum ServerState {
                Running
                Stopped
                Error
            }
            """;

        var result = await _outliner.OutlineAsync("enum.ps1", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "ServerState" && n.Kind == CodeSymbolKind.Enum));
    }

    [TestMethod]
    public async Task OutlineAsync_Variables_Extracted()
    {
        var source = """
            $ConfigPath = "C:\\config.json"
            $MaxRetries = 3

            function Do-Something { }
            """;

        var result = await _outliner.OutlineAsync("vars.ps1", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "$ConfigPath"));
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "$MaxRetries"));
    }

    [TestMethod]
    public async Task OutlineAsync_EmptySource_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.ps1", "");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }
}
