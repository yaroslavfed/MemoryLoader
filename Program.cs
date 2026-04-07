using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

class Program
{
    static void Main(string[] args)
    {
        if (!TryParseArgs(args, out LoaderOptions options, out string? error, out bool showHelp))
        {
            Console.WriteLine(error);
            Console.WriteLine();
            PrintHelp();
            return;
        }

        if (showHelp)
        {
            PrintHelp();
            return;
        }

        Run(options);
    }

    static void Run(LoaderOptions options)
    {
        long totalPhysicalBytes = GetTotalPhysicalMemory();
        long targetReservedBytes = GetTargetReservedBytes(options, totalPhysicalBytes);
        var blocks = new List<MemoryBlock>();
        bool cancelled = false;
        DateTimeOffset startedAt = DateTimeOffset.Now;
        DateTimeOffset? stopAt = options.RunTimeSeconds is int seconds
            ? startedAt.AddSeconds(seconds)
            : null;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancelled = true;
        };

        PrintBanner(options, totalPhysicalBytes, targetReservedBytes, stopAt);

        try
        {
            if (options.Mode == LoaderMode.Hold)
                RunHoldMode(options, blocks, targetReservedBytes, () => cancelled, stopAt);
            else
                RunSwingMode(options, blocks, targetReservedBytes, () => cancelled, stopAt);
        }
        finally
        {
            ReleaseAll(blocks);
            Console.WriteLine("\nStopped");
        }
    }

    static void RunHoldMode(
        LoaderOptions options,
        List<MemoryBlock> blocks,
        long targetReservedBytes,
        Func<bool> shouldStop,
        DateTimeOffset? stopAt)
    {
        FillToTarget(options, blocks, targetReservedBytes, shouldStop, stopAt);

        while (!ShouldStop(shouldStop, stopAt))
        {
            PrintStats("HOLD", blocks, targetReservedBytes, stopAt);
            Thread.Sleep(500);
        }
    }

    static void RunSwingMode(
        LoaderOptions options,
        List<MemoryBlock> blocks,
        long targetReservedBytes,
        Func<bool> shouldStop,
        DateTimeOffset? stopAt)
    {
        while (!ShouldStop(shouldStop, stopAt))
        {
            FillToTarget(options, blocks, targetReservedBytes, shouldStop, stopAt);

            if (ShouldStop(shouldStop, stopAt))
                break;

            WaitPhase("HOLD", options.HoldSeconds, blocks, targetReservedBytes, shouldStop, stopAt);

            if (ShouldStop(shouldStop, stopAt))
                break;

            Console.WriteLine("\nReleasing...");
            ReleaseAll(blocks);
            WaitPhase("RELEASE", options.ReleaseSeconds, blocks, targetReservedBytes, shouldStop, stopAt);
            Console.WriteLine("\nCycle complete\n");
        }
    }

    static void FillToTarget(
        LoaderOptions options,
        List<MemoryBlock> blocks,
        long targetReservedBytes,
        Func<bool> shouldStop,
        DateTimeOffset? stopAt)
    {
        long chunkBytes = ToBytesFromMb(options.ChunkSizeMb);

        while (!ShouldStop(shouldStop, stopAt))
        {
            long reservedBytes = GetReservedBytes(blocks);
            if (reservedBytes >= targetReservedBytes)
                break;

            long remainingBytes = targetReservedBytes - reservedBytes;
            long nextChunkBytes = Math.Min(chunkBytes, remainingBytes);
            nextChunkBytes = AlignToPage(Math.Max(nextChunkBytes, 4096));

            try
            {
                blocks.Add(AllocateAndTouch(nextChunkBytes));
            }
            catch (OutOfMemoryException)
            {
                Console.WriteLine("\nAllocation failed: not enough memory to reach target.");
                break;
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"\nAllocation failed: {ex.Message}");
                break;
            }

            PrintStats("FILL", blocks, targetReservedBytes, stopAt);
            Thread.Sleep(150);
        }
    }

    static void WaitPhase(
        string phase,
        int seconds,
        List<MemoryBlock> blocks,
        long targetReservedBytes,
        Func<bool> shouldStop,
        DateTimeOffset? stopAt)
    {
        if (seconds <= 0)
            return;

        DateTimeOffset phaseEnd = DateTimeOffset.Now.AddSeconds(seconds);

        while (!ShouldStop(shouldStop, stopAt) && DateTimeOffset.Now < phaseEnd)
        {
            PrintStats(phase, blocks, targetReservedBytes, stopAt);
            Thread.Sleep(500);
        }
    }

    static MemoryBlock AllocateAndTouch(long size)
    {
        IntPtr ptr = Marshal.AllocHGlobal(new IntPtr(size));

        try
        {
            for (long offset = 0; offset < size; offset += 4096)
                Marshal.WriteByte(ptr, (int)offset, 1);

            Marshal.WriteByte(ptr, (int)(size - 1), 1);
            return new MemoryBlock(ptr, size);
        }
        catch
        {
            Marshal.FreeHGlobal(ptr);
            throw;
        }
    }

    static void ReleaseAll(List<MemoryBlock> blocks)
    {
        foreach (MemoryBlock block in blocks)
            Marshal.FreeHGlobal(block.Pointer);

        blocks.Clear();
    }

    static bool TryParseArgs(string[] args, out LoaderOptions options, out string? error, out bool showHelp)
    {
        options = new LoaderOptions();
        error = null;
        showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (IsHelpArg(arg))
            {
                showHelp = true;
                return true;
            }

            if (int.TryParse(arg, out int percent))
            {
                options.TargetPercent = Math.Clamp(percent, 1, 99);
                options.TargetMegabytes = null;
                continue;
            }

            if (TryParseNamedInt(arg, "--mb=", out int targetMb)
                || TryParseSeparateInt(args, ref i, "-mb", out targetMb))
            {
                if (targetMb <= 0)
                {
                    error = "Параметр mb должен быть больше 0.";
                    return false;
                }

                options.TargetMegabytes = targetMb;
                continue;
            }

            if (TryParseNamedInt(arg, "--chunk=", out int chunkSizeMb)
                || TryParseSeparateInt(args, ref i, "-chunk", out chunkSizeMb))
            {
                if (chunkSizeMb <= 0)
                {
                    error = "Параметр chunk должен быть больше 0.";
                    return false;
                }

                options.ChunkSizeMb = chunkSizeMb;
                continue;
            }

            if (TryParseNamedInt(arg, "--hold=", out int holdSeconds)
                || TryParseSeparateInt(args, ref i, "-hold", out holdSeconds))
            {
                if (holdSeconds < 0)
                {
                    error = "Параметр hold должен быть 0 или больше.";
                    return false;
                }

                options.HoldSeconds = holdSeconds;
                continue;
            }

            if (TryParseNamedInt(arg, "--release=", out int releaseSeconds)
                || TryParseSeparateInt(args, ref i, "-release", out releaseSeconds))
            {
                if (releaseSeconds < 0)
                {
                    error = "Параметр release должен быть 0 или больше.";
                    return false;
                }

                options.ReleaseSeconds = releaseSeconds;
                continue;
            }

            if (TryParseNamedInt(arg, "--time=", out int runTimeSeconds)
                || TryParseSeparateInt(args, ref i, "-time", out runTimeSeconds))
            {
                if (runTimeSeconds <= 0)
                {
                    error = "Параметр time должен быть больше 0.";
                    return false;
                }

                options.RunTimeSeconds = runTimeSeconds;
                continue;
            }

            if (TryParseNamedString(arg, "--mode=", out string? modeValue)
                || TryParseSeparateString(args, ref i, "-mode", out modeValue))
            {
                if (!TryParseMode(modeValue, out LoaderMode mode))
                {
                    error = "Параметр mode должен быть hold или swing.";
                    return false;
                }

                options.Mode = mode;
                continue;
            }

            error = $"Неизвестный аргумент: {arg}";
            return false;
        }

        return true;
    }

    static bool TryParseNamedInt(string arg, string prefix, out int value)
    {
        value = 0;

        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(arg[prefix.Length..], out value);
    }

    static bool TryParseNamedString(string arg, string prefix, out string? value)
    {
        value = null;

        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        value = arg[prefix.Length..];
        return !string.IsNullOrWhiteSpace(value);
    }

    static bool TryParseSeparateInt(string[] args, ref int index, string name, out int value)
    {
        value = 0;

        if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (index + 1 >= args.Length)
            return false;

        index++;
        return int.TryParse(args[index], out value);
    }

    static bool TryParseSeparateString(string[] args, ref int index, string name, out string? value)
    {
        value = null;

        if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (index + 1 >= args.Length)
            return false;

        index++;
        value = args[index];
        return !string.IsNullOrWhiteSpace(value);
    }

    static bool TryParseMode(string? value, out LoaderMode mode)
    {
        mode = LoaderMode.Swing;

        if (value is null)
            return false;

        if (value.Equals("hold", StringComparison.OrdinalIgnoreCase)
            || value.Equals("static", StringComparison.OrdinalIgnoreCase))
        {
            mode = LoaderMode.Hold;
            return true;
        }

        if (value.Equals("swing", StringComparison.OrdinalIgnoreCase)
            || value.Equals("cycle", StringComparison.OrdinalIgnoreCase))
        {
            mode = LoaderMode.Swing;
            return true;
        }

        return false;
    }

    static bool IsHelpArg(string arg)
    {
        return arg.Equals("help", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("/?", StringComparison.OrdinalIgnoreCase);
    }

    static void PrintHelp()
    {
        Console.WriteLine("MemoryLoader - эмуляция процесса, который активно занимает ОЗУ");
        Console.WriteLine();
        Console.WriteLine("Как запускать:");
        Console.WriteLine("  dotnet run -- [процент] [-mb MB] [-mode hold|swing] [-time сек] [-chunk MB]");
        Console.WriteLine("  MemoryLoader.exe [процент] [-mb MB] [-mode hold|swing] [-time сек] [-chunk MB]");
        Console.WriteLine();
        Console.WriteLine("Аргументы:");
        Console.WriteLine("  [процент]       Сколько процентов от всей физической RAM должен занять");
        Console.WriteLine("                  сам MemoryLoader.");
        Console.WriteLine("  -mb 2048        Сколько мегабайт памяти должен занять MemoryLoader.");
        Console.WriteLine("                  Если указан, используется вместо процента.");
        Console.WriteLine("  -mode hold      Занять память и держать её, не освобождая.");
        Console.WriteLine("  -mode swing     Занимать и освобождать память по кругу.");
        Console.WriteLine("  -time 60        Работать 60 секунд и завершиться автоматически.");
        Console.WriteLine("                  Если не указывать, программа работает до ручного закрытия.");
        Console.WriteLine("  -chunk 50       Размер одного блока выделения в MB.");
        Console.WriteLine("  -hold 5         В режиме swing: сколько секунд держать память.");
        Console.WriteLine("  -release 5      В режиме swing: сколько секунд ждать после освобождения.");
        Console.WriteLine("  help            Показать эту справку.");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  dotnet run -- 40 -mode hold");
        Console.WriteLine("  dotnet run -- -mb 2048 -mode hold -time 60");
        Console.WriteLine("  dotnet run -- 30 -mode swing -hold 10 -release 3");
        Console.WriteLine("  dotnet run -- -mb 1024 -mode swing -time 120 -hold 5 -release 2");
        Console.WriteLine("  MemoryLoader.exe 25 -mode swing -time 120 -chunk 32");
        Console.WriteLine();
        Console.WriteLine("Значения по умолчанию:");
        Console.WriteLine("  Процент: 60");
        Console.WriteLine("  MB: не задано");
        Console.WriteLine("  Mode: swing");
        Console.WriteLine("  Hold: 5 секунд");
        Console.WriteLine("  Release: 5 секунд");
        Console.WriteLine("  Chunk: 50 MB");
    }

    static void PrintBanner(LoaderOptions options, long totalPhysicalBytes, long targetReservedBytes, DateTimeOffset? stopAt)
    {
        Console.WriteLine("MemoryLoader started");
        Console.WriteLine($"Mode: {options.Mode.ToString().ToLowerInvariant()}");
        Console.WriteLine(options.TargetMegabytes is int mb
            ? $"Target reserved by loader: {mb} MB"
            : $"Target reserved by loader: {options.TargetPercent}% of RAM");
        Console.WriteLine($"Total RAM: {Format(totalPhysicalBytes)}");
        Console.WriteLine($"Target reserved: {Format(targetReservedBytes)}");
        Console.WriteLine($"Chunk size: {options.ChunkSizeMb} MB");

        if (options.Mode == LoaderMode.Swing)
            Console.WriteLine($"Hold: {options.HoldSeconds}s | Release: {options.ReleaseSeconds}s");

        Console.WriteLine(stopAt is null
            ? "Run time: until manual stop"
            : $"Run time: until {stopAt:HH:mm:ss}");
        Console.WriteLine("Press Ctrl+C to stop");
        Console.WriteLine();
    }

    static void PrintStats(string phase, List<MemoryBlock> blocks, long targetReservedBytes, DateTimeOffset? stopAt)
    {
        long processUsed = Process.GetCurrentProcess().WorkingSet64;
        long reserved = GetReservedBytes(blocks);
        string timeLeft = stopAt is null
            ? "manual"
            : Math.Max(0, (int)(stopAt.Value - DateTimeOffset.Now).TotalSeconds) + "s";

        string line =
            $"{phase} | Reserved: {Format(reserved)} / {Format(targetReservedBytes)} | " +
            $"Process WS: {Format(processUsed)} | Blocks: {blocks.Count} | Left: {timeLeft}     ";

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(line);
            return;
        }

        Console.CursorLeft = 0;
        Console.Write(line);
    }

    static bool ShouldStop(Func<bool> shouldStop, DateTimeOffset? stopAt)
    {
        return shouldStop() || (stopAt is not null && DateTimeOffset.Now >= stopAt.Value);
    }

    static long GetReservedBytes(List<MemoryBlock> blocks)
    {
        long total = 0;

        foreach (MemoryBlock block in blocks)
            total += block.Size;

        return total;
    }

    static long GetTotalPhysicalMemory()
    {
        MemoryStatus status = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatus>()
        };

        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return (long)status.TotalPhysical;
    }

    static long GetTargetReservedBytes(LoaderOptions options, long totalPhysicalBytes)
    {
        if (options.TargetMegabytes is int mb)
            return Math.Min(ToBytesFromMb(mb), totalPhysicalBytes);

        return totalPhysicalBytes * options.TargetPercent / 100;
    }

    static long ToBytesFromMb(int megabytes)
    {
        return megabytes * 1024L * 1024L;
    }

    static long AlignToPage(long value)
    {
        const int pageSize = 4096;
        return ((value + pageSize - 1) / pageSize) * pageSize;
    }

    static string Format(long bytes)
    {
        double gb = bytes / 1024.0 / 1024 / 1024;
        return $"{gb:F2} GB";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    class LoaderOptions
    {
        public int TargetPercent { get; set; } = 60;
        public int? TargetMegabytes { get; set; }
        public LoaderMode Mode { get; set; } = LoaderMode.Swing;
        public int HoldSeconds { get; set; } = 5;
        public int ReleaseSeconds { get; set; } = 5;
        public int ChunkSizeMb { get; set; } = 50;
        public int? RunTimeSeconds { get; set; }
    }

    enum LoaderMode
    {
        Hold,
        Swing
    }

    readonly record struct MemoryBlock(IntPtr Pointer, long Size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus lpBuffer);
}
