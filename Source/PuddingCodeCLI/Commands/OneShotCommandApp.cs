using Spectre.Console;
using Spectre.Console.Cli;

namespace PuddingCodeCLI.Commands;

internal static class OneShotCommandApp
{
    public static bool TryRun(string[] args, string configPath, string workspaceRoot)
    {
        if (args.Length == 0)
            return false;

        var root = args[0].ToLowerInvariant();
        if (root is not ("provider" or "providers" or "model" or "models"))
            return false;

        if (root is "model" or "models")
            args[0] = "provider";

        var app = new CommandApp();
        app.Configure(cfg =>
        {
            cfg.SetApplicationName("pudding");
            cfg.AddBranch("provider", branch =>
            {
                branch.SetDescription("Provider management commands");
                branch.AddCommand<ProviderListCommand>("list")
                    .WithDescription("List configured providers");
                branch.AddCommand<ProviderUseCommand>("use")
                    .WithDescription("Switch active provider");
                branch.AddCommand<ProviderAddCommand>("add")
                    .WithDescription("Add a provider");
                branch.AddCommand<ProviderRemoveCommand>("remove")
                    .WithDescription("Remove a provider");
            });
        });

        ProviderCommandRuntime.ConfigPath = configPath;
        ProviderCommandRuntime.WorkspaceRoot = workspaceRoot;
        app.Run(args);
        return true;
    }

    private static class ProviderCommandRuntime
    {
        public static string ConfigPath { get; set; } = ConfigManager.DefaultPath;
        public static string WorkspaceRoot { get; set; } = Environment.CurrentDirectory;
    }

    private sealed class ProviderUseSettings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ProviderAddSettings : CommandSettings
    {
        [CommandOption("--id <ID>")]
        public string Id { get; set; } = string.Empty;

        [CommandOption("--name <NAME>")]
        public string Name { get; set; } = string.Empty;

        [CommandOption("--endpoint <URL>")]
        public string Endpoint { get; set; } = string.Empty;

        [CommandOption("--key <KEY>")]
        public string ApiKey { get; set; } = string.Empty;

        [CommandOption("--model <MODEL>")]
        public string Model { get; set; } = string.Empty;

        [CommandOption("--temp <VALUE>")]
        public double? Temperature { get; set; }

        [CommandOption("--max-tokens <VALUE>")]
        public int? MaxTokens { get; set; }

        [CommandOption("--billing <MODE>")]
        public string BillingMode { get; set; } = "per_token";

        [CommandOption("--in-usd-per-m <VALUE>")]
        public decimal? InputUsdPerMillionTokens { get; set; }

        [CommandOption("--out-usd-per-m <VALUE>")]
        public decimal? OutputUsdPerMillionTokens { get; set; }

        [CommandOption("--request-usd <VALUE>")]
        public decimal? RequestUsd { get; set; }

        [CommandOption("--session-usd <VALUE>")]
        public decimal? SessionUsd { get; set; }

        [CommandOption("--monthly-usd <VALUE>")]
        public decimal? MonthlyUsd { get; set; }

        [CommandOption("--included-requests <VALUE>")]
        public int? IncludedRequestsPerMonth { get; set; }

        [CommandOption("--included-sessions <VALUE>")]
        public int? IncludedSessionsPerMonth { get; set; }
    }

    private sealed class ProviderRemoveSettings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ProviderListSettings : CommandSettings;

    private sealed class ProviderListCommand : Command<ProviderListSettings>
    {
        public override int Execute(CommandContext context, ProviderListSettings settings)
        {
            var config = ConfigManager.Load(ProviderCommandRuntime.ConfigPath, ProviderCommandRuntime.WorkspaceRoot);
            if (config.Providers.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No providers configured.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("")
                .AddColumn("ID")
                .AddColumn("Model")
                .AddColumn("Endpoint");

            foreach (var p in config.Providers)
            {
                var isActive = p.Id.Equals(config.ActiveProvider, StringComparison.OrdinalIgnoreCase);
                table.AddRow(
                    isActive ? "[green]*[/]" : "[grey]-[/]",
                    p.Id.EscapeMarkup(),
                    p.Model.EscapeMarkup(),
                    p.Endpoint.EscapeMarkup());
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    private sealed class ProviderUseCommand : Command<ProviderUseSettings>
    {
        public override int Execute(CommandContext context, ProviderUseSettings settings)
        {
            var id = settings.Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                AnsiConsole.MarkupLine("[red]Provider id is required.[/]");
                return -1;
            }

            var config = ConfigManager.Load(ProviderCommandRuntime.ConfigPath, ProviderCommandRuntime.WorkspaceRoot);
            if (!ProviderConfigService.TrySetActive(config, id, out var target, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                return -1;
            }

            ConfigManager.Save(ProviderCommandRuntime.ConfigPath, config);
            AnsiConsole.MarkupLine(
                $"[green]Active provider switched:[/] [yellow]{target!.Id.EscapeMarkup()}[/] ({target.Model.EscapeMarkup()})");
            return 0;
        }
    }

    private sealed class ProviderAddCommand : Command<ProviderAddSettings>
    {
        public override int Execute(CommandContext context, ProviderAddSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.Id) ||
                string.IsNullOrWhiteSpace(s.Endpoint) ||
                string.IsNullOrWhiteSpace(s.ApiKey) ||
                string.IsNullOrWhiteSpace(s.Model))
            {
                AnsiConsole.MarkupLine(
                    "[red]Missing required options.[/] Required: --id --endpoint --key --model");
                return -1;
            }

            var name = string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name;
            var entry = new ProviderEntry
            {
                Id = s.Id.Trim(),
                Name = name.Trim(),
                Endpoint = s.Endpoint.Trim(),
                ApiKey = s.ApiKey.Trim(),
                Model = s.Model.Trim(),
                Temperature = s.Temperature,
                MaxTokens = s.MaxTokens,
                Billing = BuildBilling(s)
            };

            var config = ConfigManager.Load(ProviderCommandRuntime.ConfigPath, ProviderCommandRuntime.WorkspaceRoot);
            if (!ProviderConfigService.TryAdd(config, entry, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                return -1;
            }

            ConfigManager.Save(ProviderCommandRuntime.ConfigPath, config);
            AnsiConsole.MarkupLine(
                $"[green]Provider added:[/] [yellow]{entry.Id.EscapeMarkup()}[/] ({entry.Model.EscapeMarkup()})");
            return 0;
        }
    }

    private sealed class ProviderRemoveCommand : Command<ProviderRemoveSettings>
    {
        public override int Execute(CommandContext context, ProviderRemoveSettings s)
        {
            var id = s.Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                AnsiConsole.MarkupLine("[red]Provider id is required.[/]");
                return -1;
            }

            var config = ConfigManager.Load(ProviderCommandRuntime.ConfigPath, ProviderCommandRuntime.WorkspaceRoot);
            if (!ProviderConfigService.TryRemove(config, id, out _, out var switchedTo, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                return -1;
            }

            ConfigManager.Save(ProviderCommandRuntime.ConfigPath, config);
            AnsiConsole.MarkupLine($"[green]Provider removed:[/] {id.EscapeMarkup()}");
            if (!string.IsNullOrWhiteSpace(switchedTo))
                AnsiConsole.MarkupLine($"[grey]Active provider switched to {switchedTo.EscapeMarkup()}[/]");
            return 0;
        }
    }

    private static ProviderBillingConfig BuildBilling(ProviderAddSettings s)
    {
        var mode = s.BillingMode.Trim().ToLowerInvariant() switch
        {
            "per_request" => BillingMode.PerRequest,
            "per_session" => BillingMode.PerSession,
            "monthly_flat" => BillingMode.MonthlyFlat,
            "local_free" => BillingMode.LocalFree,
            _ => BillingMode.PerToken
        };

        return new ProviderBillingConfig
        {
            Mode = mode,
            InputUsdPerMillionTokens = s.InputUsdPerMillionTokens ?? 0,
            OutputUsdPerMillionTokens = s.OutputUsdPerMillionTokens ?? 0,
            RequestUsd = s.RequestUsd ?? 0,
            SessionUsd = s.SessionUsd ?? 0,
            MonthlyUsd = s.MonthlyUsd ?? 0,
            IncludedRequestsPerMonth = s.IncludedRequestsPerMonth ?? 0,
            IncludedSessionsPerMonth = s.IncludedSessionsPerMonth ?? 0
        };
    }
}
