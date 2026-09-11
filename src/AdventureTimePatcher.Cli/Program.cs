using AdventureTimePatcher.Core;

internal static class Program
{
    private const string Product = "AdventureTimePatcher";
    private const string DefaultOwner = "sebbun123";
    private const string DefaultRepo = "Adventuretime";
    private const string DefaultBranch = "main";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await MainAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Product}] ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> MainAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var opt = Options.Parse(args.Skip(1).ToArray());
        if (string.IsNullOrWhiteSpace(opt.MqRoot))
        {
            Console.Error.WriteLine("Missing required --mq \"/path/to/MQ/root\".");
            PrintHelp();
            return 2;
        }

        var repo = new RepoRef(opt.Owner ?? DefaultOwner, opt.Repo ?? DefaultRepo, opt.Branch ?? DefaultBranch);
        var service = new PatcherService();

        return command switch
        {
            "check" => await CheckAsync(service, opt.MqRoot, repo).ConfigureAwait(false),
            "update" => await UpdateAsync(service, opt.MqRoot, repo, opt.Force).ConfigureAwait(false),
            _ => Unknown(command)
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(Product);
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  AdventureTimePatcher check  --mq \"/path/to/MQ/root\"");
        Console.WriteLine("  AdventureTimePatcher update --mq \"/path/to/MQ/root\"");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --mq <path>       MacroQuest root folder containing lua/ and config/");
        Console.WriteLine("  --owner <owner>   GitHub owner override, default sebbun123");
        Console.WriteLine("  --repo <repo>     GitHub repo override, default Adventuretime");
        Console.WriteLine("  --branch <name>   GitHub branch override, default main");
        Console.WriteLine("  --force           Allow replacing a newer local test build");
    }

    private static async Task<int> CheckAsync(PatcherService service, string mqRoot, RepoRef repo)
    {
        var result = await service.CheckAsync(mqRoot, repo).ConfigureAwait(false);
        Console.WriteLine($"Repo:      {repo.Owner}/{repo.Name} ({repo.Branch})");
        Console.WriteLine($"MQ root:   {result.MqRoot}");
        Console.WriteLine($"Latest:    {PatcherService.ShortSha(result.LatestSha)}");
        Console.WriteLine($"Installed: {(result.InstalledSha.Length > 0 ? PatcherService.ShortSha(result.InstalledSha) : "none")}");
        Console.WriteLine($"Status:    {(result.UpdateAvailable ? "update available" : "up to date")}");
        return result.UpdateAvailable ? 10 : 0;
    }

    private static async Task<int> UpdateAsync(PatcherService service, string mqRoot, RepoRef repo, bool force)
    {
        var progress = new Progress<string>(line => Console.WriteLine($"[{Product}] {line}"));
        var result = await service.UpdateAsync(mqRoot, repo, progress, allowLocalNewer: force).ConfigureAwait(false);
        if (result.AlreadyCurrent)
        {
            Console.WriteLine($"[{Product}] AdventureTime is already current ({PatcherService.ShortSha(result.LatestSha)}).");
            return 0;
        }
        Console.WriteLine($"[{Product}] Installed AdventureTime {PatcherService.ShortSha(result.LatestSha)}.");
        Console.WriteLine($"MQ root: {result.MqRoot}");
        foreach (var file in result.CopiedFiles) Console.WriteLine($"  updated {file}");
        if (!string.IsNullOrWhiteSpace(result.BackupDir)) Console.WriteLine($"Backup: {result.BackupDir}");
        return 0;
    }
}

internal sealed class Options
{
    public string? MqRoot { get; private init; }
    public string? Owner { get; private init; }
    public string? Repo { get; private init; }
    public string? Branch { get; private init; }
    public bool Force { get; private init; }

    public static Options Parse(string[] args)
    {
        string? mq = null, owner = null, repo = null, branch = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {a}");
                return args[++i];
            }

            switch (a)
            {
                case "--mq": mq = Next(); break;
                case "--owner": owner = Next(); break;
                case "--repo": repo = Next(); break;
                case "--branch": branch = Next(); break;
                case "--force": force = true; break;
                default: throw new ArgumentException($"Unknown option: {a}");
            }
        }
        return new Options { MqRoot = mq, Owner = owner, Repo = repo, Branch = branch, Force = force };
    }
}
