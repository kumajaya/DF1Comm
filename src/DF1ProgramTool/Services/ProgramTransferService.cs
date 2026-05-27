using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DF1Comm;

namespace DF1ProgramTool.Services;

public class ProgramTransferService
{
    private readonly global::DF1Comm.DF1Comm _df1;
    private readonly IProgress<string>? _progressMessage;
    private readonly IProgress<double>? _progressPercent;
    private readonly CancellationToken _cancellationToken;

    public ProgramTransferService(
        global::DF1Comm.DF1Comm df1,
        IProgress<string>? message = null,
        IProgress<double>? percent = null,
        CancellationToken cancellation = default)
    {
        _df1 = df1 ?? throw new ArgumentNullException(nameof(df1));
        _progressMessage = message;
        _progressPercent = percent;
        _cancellationToken = cancellation;
    }

    public async Task UploadToFileAsync(string filePath)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _progressMessage?.Report("Reading program directory…");

            // Subscribe before calling UploadProgramData so we can count events
            int completedSteps = 0;
            // DF1Comm fires UploadProgress once per program file written; there is no
            // pre-known total, so we report indeterminate progress until the call returns.
            EventHandler progressHandler = (_, _) =>
            {
                completedSteps++;
                _progressMessage?.Report($"Uploading file {completedSteps}…");
                // Indeterminate: pulse the bar in increments of 5, capped at 90
                double pct = Math.Min(completedSteps * 5.0, 90.0);
                _progressPercent?.Report(pct);
            };

            _df1.UploadProgress += progressHandler;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var files = _df1.UploadProgramData();

                _progressPercent?.Report(95);
                _progressMessage?.Report($"Saving {files.Count} file(s) to disk…");
                SaveToFile(filePath, files);

                _progressPercent?.Report(100);
                _progressMessage?.Report($"Upload complete – {files.Count} program file(s).");
            }
            finally
            {
                _df1.UploadProgress -= progressHandler;
            }
        }, _cancellationToken);
    }

    public async Task DownloadFromFileAsync(string filePath)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _progressMessage?.Report("Reading program file from disk…");
            var files = LoadFromFile(filePath);

            // DF1Comm.SetProgramMode() throws on failure; let the exception propagate
            _progressMessage?.Report("Setting PLC to Program mode…");
            _df1.SetProgramMode();

            _cancellationToken.ThrowIfCancellationRequested();

            int totalSteps = 0;
            int completedSteps = 0;

            EventHandler progressHandler = (_, _) =>
            {
                completedSteps++;
                double pct = totalSteps > 0
                    ? (double)completedSteps / totalSteps * 95.0
                    : Math.Min(completedSteps * 5.0, 90.0);
                _progressPercent?.Report(pct);
                _progressMessage?.Report($"Downloading step {completedSteps}" +
                    (totalSteps > 0 ? $" of {totalSteps}…" : "…"));
            };

            _df1.DownloadProgress += progressHandler;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _df1.DownloadProgramData(files);

                _progressPercent?.Report(100);
                _progressMessage?.Report("Download complete.");
            }
            finally
            {
                _df1.DownloadProgress -= progressHandler;
            }
        }, _cancellationToken);
    }

    // ─── Binary serialisation ─────────────────────────────────────────────────

    private static void SaveToFile(string path, Collection<PLCFileDetails> files)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // Header: magic + version for forward-compatibility
        bw.Write((ushort)0xDF1A); // magic
        bw.Write((byte)1);        // format version
        bw.Write(files.Count);

        foreach (var file in files)
        {
            bw.Write(file.FileNumber);
            bw.Write(file.FileType);      // int32
            bw.Write(file.NumberOfBytes);
            bw.Write(file.Data.Length);
            bw.Write(file.Data);
        }
    }

    private static Collection<PLCFileDetails> LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Program file not found: {path}");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        // Support both legacy (no header) and versioned files
        // Peek at the first two bytes to detect the magic marker
        ushort magic = br.ReadUInt16();
        int count;
        bool versioned = magic == 0xDF1A;
        if (versioned)
        {
            byte version = br.ReadByte(); // reserved for future use
            _ = version;
            count = br.ReadInt32();
        }
        else
        {
            // Legacy format: the first 4 bytes are a little-endian int32 count.
            // Re-read: we already consumed 2 bytes, read the remaining 2.
            ushort hi = br.ReadUInt16();
            count = (int)((uint)hi << 16 | magic);
        }

        if (count < 0 || count > 10_000)
            throw new InvalidDataException($"Unexpected file count: {count}. File may be corrupt.");

        var files = new Collection<PLCFileDetails>();
        for (int i = 0; i < count; i++)
        {
            var file = new PLCFileDetails
            {
                FileNumber    = br.ReadInt32(),
                FileType      = br.ReadInt32(),   // int32, not byte
                NumberOfBytes = br.ReadInt32()
            };
            int dataLen = br.ReadInt32();
            if (dataLen < 0 || dataLen > 1_000_000)
                throw new InvalidDataException($"Invalid data length at file {i}: {dataLen}");
            file.Data = br.ReadBytes(dataLen);
            files.Add(file);
        }
        return files;
    }
}
