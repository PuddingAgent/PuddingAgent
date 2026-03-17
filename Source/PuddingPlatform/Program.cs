using PuddingPlatform.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// ── 注册 PlatformApiClient（通过 Controller API 操作控制面）──
builder.Services.AddHttpClient<PlatformApiClient>(client =>
{
    var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
