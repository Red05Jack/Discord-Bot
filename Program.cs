using DiscordXpBot.Configuration;
using DiscordXpBot.Data;
using DiscordXpBot.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var configPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("BOT_CONFIG_PATH") ?? "appsettings.json";

try
{
    var options = BotOptions.Load(configPath);
    var database = new BotDatabase(options.Xp.DatabasePath);
    await database.InitializeAsync();

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

    await using var bot = new DiscordXpBotService(options, database);
    await bot.RunAsync(shutdown.Token);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
