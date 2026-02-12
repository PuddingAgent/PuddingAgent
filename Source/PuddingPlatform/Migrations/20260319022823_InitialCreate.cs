using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PuddingPlatform.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "AppRoles",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PermissionsJson = table.Column<string>(type: "text", nullable: false),
                    IsSystemRole = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UserType = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalAgentTemplates",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    UserPromptTemplate = table.Column<string>(type: "text", nullable: true),
                    PreferredProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreferredModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MaxContextTokens = table.Column<int>(type: "integer", nullable: false),
                    MaxReplyTokens = table.Column<int>(type: "integer", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalAgentTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlmProviders",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceAgentTemplates",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TemplateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    UserPromptTemplate = table.Column<string>(type: "text", nullable: true),
                    PreferredProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreferredModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MaxContextTokens = table.Column<int>(type: "integer", nullable: false),
                    MaxReplyTokens = table.Column<int>(type: "integer", nullable: false),
                    BaseGlobalTemplateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceAgentTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUserRoles",
                schema: "platform",
                columns: table => new
                {
                    UserEntityId = table.Column<int>(type: "integer", nullable: false),
                    RoleEntityId = table.Column<int>(type: "integer", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserRoles", x => new { x.UserEntityId, x.RoleEntityId });
                    table.ForeignKey(
                        name: "FK_AppUserRoles_AppRoles_RoleEntityId",
                        column: x => x.RoleEntityId,
                        principalSchema: "platform",
                        principalTable: "AppRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserRoles_AppUsers_UserEntityId",
                        column: x => x.UserEntityId,
                        principalSchema: "platform",
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LlmModels",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderId = table.Column<int>(type: "integer", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    MaxContextTokens = table.Column<int>(type: "integer", nullable: false),
                    InputPricePer1MTokens = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    OutputPricePer1MTokens = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    CapabilityTagsJson = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IsDeprecated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmModels_LlmProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "platform",
                        principalTable: "LlmProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LlmProviderQuotas",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderId = table.Column<int>(type: "integer", nullable: false),
                    DailyTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    DailyTokensUsed = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyTokensUsed = table.Column<long>(type: "bigint", nullable: false),
                    IsSuspended = table.Column<bool>(type: "boolean", nullable: false),
                    DailyResetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MonthlyResetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviderQuotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmProviderQuotas_LlmProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "platform",
                        principalTable: "LlmProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                schema: "platform",
                columns: table => new
                {
                    TeamEntityId = table.Column<int>(type: "integer", nullable: false),
                    UserEntityId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => new { x.TeamEntityId, x.UserEntityId });
                    table.ForeignKey(
                        name: "FK_TeamMembers_AppUsers_UserEntityId",
                        column: x => x.UserEntityId,
                        principalSchema: "platform",
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TeamEntityId",
                        column: x => x.TeamEntityId,
                        principalSchema: "platform",
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TeamEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TeamAccessPolicy = table.Column<int>(type: "integer", nullable: false),
                    CompanyAccessPolicy = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsFrozen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_Teams_TeamEntityId",
                        column: x => x.TeamEntityId,
                        principalSchema: "platform",
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBases",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    KbId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    KbType = table.Column<string>(type: "text", nullable: false),
                    DocumentCount = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeBases_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DefinitionJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workflows_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceAgents",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SourceTemplateId = table.Column<string>(type: "text", nullable: true),
                    SystemPromptOverride = table.Column<string>(type: "text", nullable: true),
                    PreferredProviderId = table.Column<string>(type: "text", nullable: true),
                    PreferredModelId = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsFrozen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceAgents_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceChannels",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ChannelType = table.Column<string>(type: "text", nullable: false),
                    DefaultAgentId = table.Column<string>(type: "text", nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceChannels_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    UserEntityId = table.Column<int>(type: "integer", nullable: false),
                    AccessLevel = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_AppUsers_UserEntityId",
                        column: x => x.UserEntityId,
                        principalSchema: "platform",
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceSkills",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SkillId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceEntityId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SkillType = table.Column<string>(type: "text", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceSkills_Workspaces_WorkspaceEntityId",
                        column: x => x.WorkspaceEntityId,
                        principalSchema: "platform",
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "AppRoles",
                columns: new[] { "Id", "CreatedAt", "Description", "IsSystemRole", "Name", "PermissionsJson", "RoleId", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "可管理所属 Workspace 的配置、成员和 Agent 模板", true, "Workspace 管理员", "[\"workspace:manage\",\"workspace:write\",\"workspace:read\",\"agent:manage\",\"template:manage\"]", "workspace-admin", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 2, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "可在 Workspace 内创建/使用 Session 和 Agent", true, "Workspace 编辑", "[\"workspace:write\",\"workspace:read\",\"agent:run\",\"template:read\"]", "workspace-editor", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 3, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "只读访问 Workspace 内容", true, "Workspace 查看者", "[\"workspace:read\",\"template:read\"]", "workspace-viewer", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 4, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "可管理 LLM 资源池（服务商/模型/配额）", true, "LLM 资源管理员", "[\"llm:manage\",\"llm:read\"]", "llm-admin", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "GlobalAgentTemplates",
                columns: new[] { "Id", "CreatedAt", "Description", "IsBuiltIn", "IsEnabled", "MaxContextTokens", "MaxReplyTokens", "Name", "PreferredModelId", "PreferredProviderId", "Role", "SortOrder", "SystemPrompt", "TemplateId", "UpdatedAt", "UserPromptTemplate" },
                values: new object[] { 1, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "通用型对话助手，适合日常问答、文案写作等场景。", true, true, 32768, 2048, "通用助手", "gpt-4o-mini", "openai", "Service", 10, "你是一个专业、友好的 AI 助手。请直接、准确地回答用户的问题。", "general-assistant", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "LlmProviders",
                columns: new[] { "Id", "ApiKey", "BaseUrl", "CreatedAt", "Description", "IsEnabled", "Name", "Protocol", "ProviderId", "UpdatedAt" },
                values: new object[] { 1, null, "https://api.openai.com/v1", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "OpenAI 官方 API", true, "OpenAI", "openai", "openai", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "Teams",
                columns: new[] { "Id", "CreatedAt", "Description", "IsEnabled", "Name", "TeamId", "UpdatedAt" },
                values: new object[] { 1, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "平台默认团队", true, "平台团队", "platform-team", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "LlmModels",
                columns: new[] { "Id", "CapabilityTagsJson", "CreatedAt", "Description", "InputPricePer1MTokens", "IsDefault", "IsDeprecated", "MaxContextTokens", "ModelId", "Name", "OutputPricePer1MTokens", "ProviderId", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "[\"text\",\"vision\",\"function-calling\",\"json-mode\"]", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "轻量级多模态模型，适合一般对话和任务，性价比高。", 0.15m, true, false, 128000, "gpt-4o-mini", "GPT-4o Mini", 0.60m, 1, 10, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 2, "[\"text\",\"vision\",\"function-calling\",\"json-mode\"]", new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "高性能多模态旗舰模型，支持视觉、函数调用与结构化输出。", 5.00m, false, false, 128000, "gpt-4o", "GPT-4o", 15.00m, 1, 20, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "Workspaces",
                columns: new[] { "Id", "CompanyAccessPolicy", "CreatedAt", "Description", "IsEnabled", "IsFrozen", "Name", "Slug", "TeamAccessPolicy", "TeamEntityId", "UpdatedAt", "WorkspaceId" },
                values: new object[] { 1, 1, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "平台内置默认工作空间", true, false, "默认工作空间", "default", 2, 1, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "default" });

            migrationBuilder.CreateIndex(
                name: "IX_AppRoles_RoleId",
                schema: "platform",
                table: "AppRoles",
                column: "RoleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserRoles_RoleEntityId",
                schema: "platform",
                table: "AppUserRoles",
                column: "RoleEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Email",
                schema: "platform",
                table: "AppUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_UserId",
                schema: "platform",
                table: "AppUsers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlobalAgentTemplates_TemplateId",
                schema: "platform",
                table: "GlobalAgentTemplates",
                column: "TemplateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBases_KbId",
                schema: "platform",
                table: "KnowledgeBases",
                column: "KbId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBases_WorkspaceEntityId",
                schema: "platform",
                table: "KnowledgeBases",
                column: "WorkspaceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmModels_ProviderId_ModelId",
                schema: "platform",
                table: "LlmModels",
                columns: new[] { "ProviderId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviderQuotas_ProviderId",
                schema: "platform",
                table: "LlmProviderQuotas",
                column: "ProviderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviders_ProviderId",
                schema: "platform",
                table: "LlmProviders",
                column: "ProviderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_UserEntityId",
                schema: "platform",
                table: "TeamMembers",
                column: "UserEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_TeamId",
                schema: "platform",
                table: "Teams",
                column: "TeamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_WorkflowId",
                schema: "platform",
                table: "Workflows",
                column: "WorkflowId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_WorkspaceEntityId",
                schema: "platform",
                table: "Workflows",
                column: "WorkspaceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAgents_AgentId",
                schema: "platform",
                table: "WorkspaceAgents",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAgents_WorkspaceEntityId",
                schema: "platform",
                table: "WorkspaceAgents",
                column: "WorkspaceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAgentTemplates_WorkspaceId_TemplateId",
                schema: "platform",
                table: "WorkspaceAgentTemplates",
                columns: new[] { "WorkspaceId", "TemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceChannels_ChannelId",
                schema: "platform",
                table: "WorkspaceChannels",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceChannels_WorkspaceEntityId",
                schema: "platform",
                table: "WorkspaceChannels",
                column: "WorkspaceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserEntityId",
                schema: "platform",
                table: "WorkspaceMembers",
                column: "UserEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_WorkspaceEntityId_UserEntityId",
                schema: "platform",
                table: "WorkspaceMembers",
                columns: new[] { "WorkspaceEntityId", "UserEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TeamEntityId_Slug",
                schema: "platform",
                table: "Workspaces",
                columns: new[] { "TeamEntityId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_WorkspaceId",
                schema: "platform",
                table: "Workspaces",
                column: "WorkspaceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceSkills_SkillId",
                schema: "platform",
                table: "WorkspaceSkills",
                column: "SkillId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceSkills_WorkspaceEntityId",
                schema: "platform",
                table: "WorkspaceSkills",
                column: "WorkspaceEntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppUserRoles",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "GlobalAgentTemplates",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "KnowledgeBases",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "LlmModels",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "LlmProviderQuotas",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TeamMembers",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Workflows",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "WorkspaceAgents",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "WorkspaceAgentTemplates",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "WorkspaceChannels",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "WorkspaceMembers",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "WorkspaceSkills",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "AppRoles",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "LlmProviders",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "AppUsers",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Workspaces",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Teams",
                schema: "platform");
        }
    }
}
