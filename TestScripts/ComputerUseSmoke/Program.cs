using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PuddingComputer.Windows;
using PuddingCode.Tools;
using Pudding.Contracts.Computer;
using PuddingRuntime.Services.Tools;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int show);
    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var form = new Form { Text = "Pudding Computer Use Chromium test", ClientSize = new(900, 500), StartPosition = FormStartPosition.Manual, Location = new(150, 180) };
        using var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        form.Shown += async (_, _) =>
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "smoke-output"));
                Directory.CreateDirectory(root);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "web-profile"));
                await web.EnsureCoreWebView2Async(env);
                var loaded = new TaskCompletionSource();
                web.NavigationCompleted += (_, _) => loaded.TrySetResult();
                web.NavigateToString("<html><body style='margin:0;background:#fff;font:20px Arial'><input id='edit' style='position:absolute;left:50px;top:60px;width:320px;height:44px;box-sizing:border-box;border:2px solid #555;background:#fafafa'><button id='button' style='position:absolute;left:50px;top:150px;width:200px;height:44px' onclick='document.body.dataset.count=String(+(document.body.dataset.count||0)+1)'>Click test</button></body></html>");
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
                ShowWindow(form.Handle, 5);
                form.Activate();
                var session = new ComputerDesktopSession();
                var tool = new ComputerInputTool(session);
                var context = new ToolExecutionContext { WorkspaceId = "smoke", SessionId = "smoke", AgentInstanceId = "smoke" };
                var owner = JsonSerializer.Serialize(new[] { "smoke", "smoke", "smoke" });
                var hwnd = form.Handle.ToInt64();
                async Task<ToolExecutionResult> Call(object args)
                {
                    var result = await Task.Run(() => tool.ExecuteAsync(new ToolExecutionRequest { ToolCallId = Guid.NewGuid().ToString("N"), ArgumentsJson = JsonSerializer.Serialize(args), Context = context }));
                    Console.WriteLine(result.Output);
                    return result;
                }
                void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
                // All interaction is through production native input. JavaScript below
                // only reads test postconditions; it never focuses, clicks or fills.
                foreach (var screen in Screen.AllScreens)
                {
                    form.Location = new(screen.WorkingArea.X + 80, screen.WorkingArea.Y + 100);
                    form.Activate();
                    await Task.Delay(300);
                    Check((await Call(new { action = "focus", handle = hwnd })).Success, "native focus");
                    var observation = await Task.Run(() => session.Observe(owner, hwnd, 512));
                    File.WriteAllBytes(Path.Combine(root, $"screen-{screen.Bounds.X}-original.png"), observation.OriginalPng);
                    File.WriteAllBytes(Path.Combine(root, $"screen-{screen.Bounds.X}-marked.png"), observation.AnnotatedPng);
                    var scale = form.DeviceDpi / 96d;
                    var px = 180 * scale * observation.Geometry.ImageWidth / observation.Geometry.SourceRect.Width;
                    var py = 82 * scale * observation.Geometry.ImageHeight / observation.Geometry.SourceRect.Height;
                    var wrongOwner = await Task.Run(() => session.ClickObservation("wrong-owner", observation.ObservationId, null, new ImagePoint(px, py)));
                    Check(wrongOwner.Receipt.Completion == ComputerCompletion.Blocked, "wrong owner blocked");
                    Check((await Call(new { action = "click", observation_id = observation.ObservationId, image_x = px, image_y = py, steps = 2 })).Success, "image point dispatched");
                    Check(!(await Call(new { action = "click", observation_id = observation.ObservationId, image_x = px, image_y = py })).Success, "single use blocked replay");
                    Check(await web.ExecuteScriptAsync("document.activeElement.id") == "\"edit\"", "Chromium editor focus after native mouse click");
                    Check((await Call(new { action = "key", handle = hwnd, keys = "Ctrl+A" })).Success, "select all dispatched");
                    Check((await Call(new { action = "type", handle = hwnd, text = "Pudding中文42" })).Success, "Unicode dispatch");
                    Check(await web.ExecuteScriptAsync("document.getElementById('edit').value") == "\"Pudding中文42\"", "Chromium Unicode value verified");
                    Check((await Call(new { action = "key", handle = hwnd, keys = "Ctrl+A" })).Success, "paste select all");
                    Check((await Call(new { action = "type", handle = hwnd, text = "粘贴Pudding", input_mode = "paste" })).Success, "paste dispatch and clipboard readback");
                    Check(await web.ExecuteScriptAsync("document.getElementById('edit').value") == "\"粘贴Pudding\"", "Chromium paste value verified");
                    var buttons = await Task.Run(() => session.Observe(owner, hwnd));
                    var target = buttons.Targets.Where(t => t.Global.X < buttons.Geometry.SourceRect.X + 150 * scale && t.Global.Y > buttons.Geometry.SourceRect.Y + 140 * scale && t.Global.Y < buttons.Geometry.SourceRect.Y + 160 * scale)
                        .OrderByDescending(t => t.Global.Width * t.Global.Height).FirstOrDefault();
                    Check(target is not null, "OpenCV button candidate found");
                    var previous = await web.ExecuteScriptAsync("document.body.dataset.count||'0'");
                    Check((await Call(new { action = "click", observation_id = buttons.ObservationId, target_id = target!.Id, steps = 2 })).Success, "candidate ID click");
                    Check(await web.ExecuteScriptAsync("document.body.dataset.count||'0'") != previous, "Chromium button changed DOM after native click");
                    var old = await Task.Run(() => session.Observe(owner, hwnd));
                    form.Left += 10;
                    var stale = await Task.Run(() => session.ClickObservation(owner, old.ObservationId, null, new ImagePoint(100, 100)));
                    Check(stale.Receipt.ErrorCode == "window_geometry_changed", "moved window observation rejected");
                    Console.WriteLine($"SCREEN VERIFIED {screen.Bounds} DPI={form.DeviceDpi}");
                }
                Console.WriteLine("CHROMIUM_SMOKE_PASS");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }
}

