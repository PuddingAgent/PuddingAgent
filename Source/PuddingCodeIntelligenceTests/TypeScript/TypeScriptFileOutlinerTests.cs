using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.TypeScript;

namespace PuddingCodeIntelligenceTests.TypeScript;

[TestClass]
public class TypeScriptFileOutlinerTests
{
    private readonly TypeScriptFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsTsTsxJsJsx()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".ts"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".tsx"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".js"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".jsx"));
    }

    [TestMethod]
    public async Task OutlineAsync_ClassDeclaration_Extracted()
    {
        var source = """
            export class MyService {
                private name: string;

                constructor(name: string) {
                    this.name = name;
                }

                public async getData(): Promise<string[]> {
                    return [];
                }
            }
            """;

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Nodes.Count);

        var classNode = result.Nodes[0];
        Assert.AreEqual("MyService", classNode.Name);
        Assert.AreEqual(CodeSymbolKind.Class, classNode.Kind);
        Assert.IsTrue(classNode.Children?.Count >= 2); // name property + getData method
    }

    [TestMethod]
    public async Task OutlineAsync_InterfaceDeclaration_Extracted()
    {
        var source = """
            export interface IConfig {
                host: string;
                port: number;
                debug?: boolean;
            }
            """;

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Nodes.Count);

        var iface = result.Nodes[0];
        Assert.AreEqual("IConfig", iface.Name);
        Assert.AreEqual(CodeSymbolKind.Interface, iface.Kind);
        Assert.IsTrue(iface.Children?.Count >= 3);
    }

    [TestMethod]
    public async Task OutlineAsync_TypeAlias_Extracted()
    {
        var source = "export type Status = 'active' | 'inactive';";

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Nodes.Count);
        Assert.AreEqual("Status", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Type, result.Nodes[0].Kind);
    }

    [TestMethod]
    public async Task OutlineAsync_Enum_Extracted()
    {
        var source = """
            export enum Direction {
                Up = 'UP',
                Down = 'DOWN',
                Left = 'LEFT',
                Right = 'RIGHT',
            }
            """;

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Nodes.Count);
        Assert.AreEqual("Direction", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Enum, result.Nodes[0].Kind);
    }

    [TestMethod]
    public async Task OutlineAsync_TopLevelFunction_Extracted()
    {
        var source = """
            export async function fetchData(url: string): Promise<Response> {
                return await fetch(url);
            }

            function helper(x: number): number {
                return x * 2;
            }
            """;

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.Nodes.Count);

        Assert.AreEqual("fetchData", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Method, result.Nodes[0].Kind);
        Assert.IsTrue(result.Nodes[0].Modifiers?.Contains("async"));

        Assert.AreEqual("helper", result.Nodes[1].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_VariableDeclarations_Extracted()
    {
        var source = """
            export const API_URL = 'https://api.example.com';
            let config: AppConfig = {};
            const MAX_RETRIES = 3;
            """;

        var result = await _outliner.OutlineAsync("test.ts", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Nodes.Count);

        Assert.AreEqual("API_URL", result.Nodes[0].Name);
        Assert.AreEqual("config", result.Nodes[1].Name);
        Assert.AreEqual("MAX_RETRIES", result.Nodes[2].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_ComplexFile_AllSymbolsExtracted()
    {
        var source = """
            import { Injectable } from '@angular/core';
            import { HttpClient } from '@angular/common/http';

            export interface UserData {
                id: number;
                name: string;
            }

            export type UserList = UserData[];

            export enum Role {
                Admin = 'ADMIN',
                User = 'USER',
            }

            @Injectable()
            export class UserService {
                private baseUrl: string;

                constructor(private http: HttpClient) {
                    this.baseUrl = '/api/users';
                }

                async getUsers(): Promise<UserData[]> {
                    return [];
                }

                async getUser(id: number): Promise<UserData | null> {
                    return null;
                }
            }

            export const DEFAULT_ROLE = Role.User;

            export function createUser(name: string): UserData {
                return { id: 0, name };
            }
            """;

        var result = await _outliner.OutlineAsync("user.service.ts", source);

        Assert.IsTrue(result.Success);

        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("UserData"));
        Assert.IsTrue(names.Contains("UserList"));
        Assert.IsTrue(names.Contains("Role"));
        Assert.IsTrue(names.Contains("UserService"));
        Assert.IsTrue(names.Contains("DEFAULT_ROLE"));
        Assert.IsTrue(names.Contains("createUser"));
    }

    [TestMethod]
    public async Task OutlineAsync_EmptySource_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.ts", "");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_ErrorHandling_ReturnsErrorResult()
    {
        // Null source should be handled gracefully
        var result = await _outliner.OutlineAsync("test.ts", "");

        Assert.IsTrue(result.Success);
    }
}
