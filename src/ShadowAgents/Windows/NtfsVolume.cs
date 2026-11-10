using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShadowAgents;

/// <summary>Where the change journal of a volume is right now.</summary>
public class UsnJournalInfo
{
    public ulong JournalId { get; set; }

    /// <summary>The oldest change still in the journal. Older positions have been overwritten.</summary>
    public long FirstUsn { get; set; }

    /// <summary>The position the next change will get.</summary>
    public long NextUsn { get; set; }
}

/// <summary>Why reading the change journal failed, so the caller can decide to run a new baseline.</summary>
public enum JournalReadProblem
{
    None,

    /// <summary>The saved position is older than the oldest entry: the journal wrapped around.</summary>
    PositionOverwritten,

    /// <summary>The journal was deleted, recreated or is not active.</summary>
    JournalReset,
}

/// <summary>
/// An open NTFS volume (such as C:). Lists every file name through the master file table, reads the
/// change journal, and turns file ids back into paths. Needs administrator or SYSTEM rights.
/// </summary>
public sealed class NtfsVolume : IDisposable
{
    private const int BufferSize = 1024 * 1024;

    private readonly SafeFileHandle volumeHandle;

    private NtfsVolume(string driveLetter, SafeFileHandle volumeHandle)
    {
        DriveLetter = driveLetter;
        this.volumeHandle = volumeHandle;
    }

    /// <summary>"C" for the C: drive.</summary>
    public string DriveLetter { get; }

    public string RootPath
    {
        get { return DriveLetter + @":\"; }
    }

    /// <summary>Opens a volume such as "C:" or "C". Returns null with a reason when it cannot (not admin, not NTFS).</summary>
    public static NtfsVolume TryOpen(string drive, out string problem)
    {
        problem = null;
        string driveLetter = drive.TrimEnd(':', '\\', '/').ToUpperInvariant();
        if (driveLetter.Length != 1)
        {
            problem = "not a drive letter";
            return null;
        }

        DriveInfo driveInfo = new DriveInfo(driveLetter);
        if (!driveInfo.IsReady || driveInfo.DriveFormat != "NTFS")
        {
            problem = "not an NTFS volume";
            return null;
        }

        SafeFileHandle handle = NativeMethods.CreateFile(
            @"\\.\" + driveLetter + ":",
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 5)
            {
                problem = "access denied (needs administrator)";
            }
            else
            {
                problem = "cannot open volume (error " + error + ")";
            }
            return null;
        }

        return new NtfsVolume(driveLetter, handle);
    }

    /// <summary>
    /// Calls <paramref name="onRecord"/> once for every file and folder on the volume, by reading the
    /// master file table. No file is opened. This is how the baseline avoids walking folders.
    /// </summary>
    public void EnumerateAllEntries(Action<UsnRecord> onRecord, CancellationToken cancellationToken)
    {
        NativeMethods.MftEnumDataV0 input = new NativeMethods.MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue,
        };
        byte[] buffer = new byte[BufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            bool succeeded = NativeMethods.DeviceIoControl(
                this.volumeHandle, NativeMethods.FsctlEnumUsnData, ref input, Marshal.SizeOf<NativeMethods.MftEnumDataV0>(),
                buffer, buffer.Length, out int bytesReturned, IntPtr.Zero);
            if (!succeeded)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorHandleEof)
                {
                    return; // listed everything
                }

                throw new Win32Exception(error);
            }

            ReadRecords(buffer, bytesReturned, onRecord);

            // The first 8 bytes of the output say where to continue.
            input.StartFileReferenceNumber = BitConverter.ToUInt64(buffer, 0);
        }
    }

    /// <summary>Returns the journal's id and positions, or null when the volume has no active journal.</summary>
    public UsnJournalInfo QueryJournal()
    {
        bool succeeded = NativeMethods.DeviceIoControl(
            this.volumeHandle, NativeMethods.FsctlQueryUsnJournal, IntPtr.Zero, 0,
            out NativeMethods.UsnJournalDataV0 journalData, Marshal.SizeOf<NativeMethods.UsnJournalDataV0>(),
            out int bytesReturned, IntPtr.Zero);
        if (!succeeded)
        {
            return null;
        }

        return new UsnJournalInfo
        {
            JournalId = journalData.UsnJournalId,
            FirstUsn = journalData.FirstUsn,
            NextUsn = journalData.NextUsn,
        };
    }

    /// <summary>
    /// Reads changes from <paramref name="startUsn"/> onwards. Only create, delete, rename and
    /// data-change reasons are returned, one record per file when it is closed.
    /// <paramref name="nextUsn"/> is where to continue next time.
    /// </summary>
    public List<UsnRecord> ReadChanges(ulong journalId, long startUsn, int maxRecords, out long nextUsn, out JournalReadProblem problem)
    {
        List<UsnRecord> records = new List<UsnRecord>();
        nextUsn = startUsn;
        problem = JournalReadProblem.None;

        uint reasonMask = UsnRecord.ReasonFileCreate | UsnRecord.ReasonFileDelete | UsnRecord.ReasonRenameNewName
            | UsnRecord.ReasonRenameOldName | UsnRecord.ReasonDataOverwrite | UsnRecord.ReasonDataExtend
            | UsnRecord.ReasonDataTruncation | UsnRecord.ReasonClose;

        NativeMethods.ReadUsnJournalDataV0 input = new NativeMethods.ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = reasonMask,
            ReturnOnlyOnClose = 1, // one record per edit, not one per write
            Timeout = 0,
            BytesToWaitFor = 0,    // return at once, do not block
            UsnJournalId = journalId,
        };
        byte[] buffer = new byte[64 * 1024];

        while (records.Count < maxRecords)
        {
            bool succeeded = NativeMethods.DeviceIoControl(
                this.volumeHandle, NativeMethods.FsctlReadUsnJournal, ref input, Marshal.SizeOf<NativeMethods.ReadUsnJournalDataV0>(),
                buffer, buffer.Length, out int bytesReturned, IntPtr.Zero);
            if (!succeeded)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorJournalEntryDeleted)
                {
                    problem = JournalReadProblem.PositionOverwritten;
                }
                else
                {
                    problem = JournalReadProblem.JournalReset;
                }
                return records;
            }

            long continueFrom = BitConverter.ToInt64(buffer, 0);
            if (bytesReturned <= 8)
            {
                nextUsn = continueFrom;
                return records; // no more changes for now
            }

            ReadRecords(buffer, bytesReturned, records.Add);
            input.StartUsn = continueFrom;
            nextUsn = continueFrom;
        }

        return records;
    }

    /// <summary>
    /// Turns a file or folder id into its full path ("C:\Users\alice\code"), or null if it no longer exists.
    /// Used to find the folder of a changed file from the parent id in a journal record.
    /// </summary>
    public string GetPathById(ulong fileReferenceNumber)
    {
        NativeMethods.FileIdDescriptor fileId = new NativeMethods.FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.FileIdDescriptor>(),
            Type = 0,
            FileId = (long)fileReferenceNumber,
        };

        using SafeFileHandle fileHandle = NativeMethods.OpenFileById(
            this.volumeHandle, ref fileId, NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero, NativeMethods.FileFlagBackupSemantics);
        if (fileHandle.IsInvalid)
        {
            return null;
        }

        char[] pathBuffer = new char[1024];
        int length = NativeMethods.GetFinalPathNameByHandle(fileHandle, pathBuffer, pathBuffer.Length, 0);
        if (length <= 0 || length > pathBuffer.Length)
        {
            return null;
        }

        string path = new string(pathBuffer, 0, length);

        // The API returns "\\?\C:\...". Remove the "\\?\" prefix.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path.Substring(4);
        }

        return path;
    }

    public void Dispose()
    {
        this.volumeHandle.Dispose();
    }

    private static void ReadRecords(byte[] buffer, int bytesReturned, Action<UsnRecord> onRecord)
    {
        int offset = 8; // skip the "continue from" value
        while (offset < bytesReturned)
        {
            UsnRecord record = UsnRecord.Parse(buffer, offset, out int recordLength);
            if (recordLength <= 0)
            {
                break; // corrupt buffer; stop rather than loop forever
            }

            if (record != null)
            {
                onRecord(record);
            }

            offset += recordLength;
        }
    }
}

/// <summary>
/// One entry from the NTFS master file table listing or the change journal: a file or folder name,
/// its id, and the id of the folder it is in. Full paths are rebuilt from the parent ids.
/// </summary>
public class UsnRecord
{
    // Bits of UsnRecord.Reason (what happened to the file).
    public const uint ReasonDataOverwrite = 0x00000001;
    public const uint ReasonDataExtend = 0x00000002;
    public const uint ReasonDataTruncation = 0x00000004;
    public const uint ReasonFileCreate = 0x00000100;
    public const uint ReasonFileDelete = 0x00000200;
    public const uint ReasonRenameOldName = 0x00001000;
    public const uint ReasonRenameNewName = 0x00002000;
    public const uint ReasonClose = 0x80000000;

    private const uint FileAttributeDirectory = 0x10;

    public ulong FileReferenceNumber { get; set; }
    public ulong ParentFileReferenceNumber { get; set; }
    public long Usn { get; set; }
    public uint Reason { get; set; }
    public uint FileAttributes { get; set; }
    public string FileName { get; set; } = "";

    public bool IsDirectory
    {
        get { return (FileAttributes & FileAttributeDirectory) != 0; }
    }

    /// <summary>
    /// Reads a USN_RECORD_V2 from <paramref name="buffer"/> at <paramref name="offset"/>.
    /// Returns null for other record versions (V3/V4 are used by ReFS, which we do not index this way).
    /// The byte offsets come from the C struct layout in winioctl.h.
    /// </summary>
    public static UsnRecord Parse(byte[] buffer, int offset, out int recordLength)
    {
        recordLength = BitConverter.ToInt32(buffer, offset);
        ushort majorVersion = BitConverter.ToUInt16(buffer, offset + 4);
        if (majorVersion != 2 || recordLength <= 0)
        {
            return null;
        }

        int fileNameLength = BitConverter.ToUInt16(buffer, offset + 56);
        int fileNameOffset = BitConverter.ToUInt16(buffer, offset + 58);

        UsnRecord record = new UsnRecord();
        record.FileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 8);
        record.ParentFileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 16);
        record.Usn = BitConverter.ToInt64(buffer, offset + 24);
        record.Reason = BitConverter.ToUInt32(buffer, offset + 40);
        record.FileAttributes = BitConverter.ToUInt32(buffer, offset + 52);
        record.FileName = Encoding.Unicode.GetString(buffer, offset + fileNameOffset, fileNameLength);
        return record;
    }

    /// <summary>
    /// The volume's root folder always has MFT record number 5. The upper 16 bits of a file reference
    /// are a sequence number, so compare only the lower 48 bits.
    /// </summary>
    public static bool IsVolumeRoot(ulong fileReferenceNumber)
    {
        const ulong recordNumberMask = 0x0000FFFFFFFFFFFF;
        return (fileReferenceNumber & recordNumberMask) == 5;
    }
}

/// <summary>
/// Declarations of the Windows API functions we call ("P/Invoke"). Each [DllImport] line says
/// "this C function lives in kernel32.dll; call it with these arguments".
/// The structs mirror the C structs byte for byte, so their field order must not change.
/// </summary>
internal static class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint FileReadAttributes = 0x80;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000; // needed to open folders

    // Device control codes for NTFS volumes (winioctl.h).
    public const uint FsctlEnumUsnData = 0x000900B3;
    public const uint FsctlQueryUsnJournal = 0x000900F4;
    public const uint FsctlReadUsnJournal = 0x000900BB;

    // Error codes we react to.
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    [StructLayout(LayoutKind.Sequential)]
    public struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileIdDescriptor
    {
        public uint Size;
        public int Type;      // 0 = a 64-bit file id
        public long FileId;
        public long Padding;  // the C struct is a 16-byte union
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte AcLineStatus;        // 0 = on battery, 1 = plugged in, 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, ref MftEnumDataV0 input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, IntPtr input, int inputSize,
        out UsnJournalDataV0 output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, ref ReadUsnJournalDataV0 input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, int pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
