using System.Runtime.InteropServices;

namespace ResourceMonitor.Sampling;

internal readonly record struct ProcessEntry(
    int Pid,
    int ParentPid,
    string Name,
    long CreateTime,
    long CpuTicks,
    long WorkingSetBytes,
    ulong IoBytes);

// Lista todo processo do sistema numa única chamada NtQuerySystemInformation
// (SystemProcessInformation) — a mesma fonte do Gerenciador de Tarefas. Não abre nenhum
// processo, então funciona sem admin também pra serviços e processos protegidos (Defender,
// lsass, svchost...), onde OpenProcess é negado mesmo com acesso mínimo; e já traz tudo que a
// varredura precisa de uma vez: CPU, memória, I/O, horário de criação e o PID do pai.
internal sealed class SystemProcessSnapshot
{
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // Reaproveitado entre ticks; cresce sozinho se o sistema tiver mais processos que cabem.
    private byte[] _buffer = new byte[512 * 1024];

    public List<ProcessEntry> Read()
    {
        while (true)
        {
            var pinned = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                var basePointer = pinned.AddrOfPinnedObject();
                var status = NtQuerySystemInformation(SystemProcessInformationClass, basePointer, _buffer.Length, out var requiredLength);
                if (status == StatusInfoLengthMismatch)
                {
                    // Folga extra: processos podem surgir entre medir o tamanho e copiar a lista.
                    _buffer = new byte[Math.Max(requiredLength, _buffer.Length) + (64 * 1024)];
                    continue;
                }

                if (status != 0)
                {
                    throw new InvalidOperationException($"NtQuerySystemInformation falhou (NTSTATUS 0x{status:X8}).");
                }

                // Lido ainda com o buffer fixado: os nomes apontam pra dentro dele.
                return Parse(basePointer);
            }
            finally
            {
                pinned.Free();
            }
        }
    }

    private static List<ProcessEntry> Parse(IntPtr basePointer)
    {
        var entries = new List<ProcessEntry>(256);
        var offset = 0;
        while (true)
        {
            var info = Marshal.PtrToStructure<SystemProcessInformation>(basePointer + offset);
            var pid = (int)info.UniqueProcessId;

            entries.Add(new ProcessEntry(
                pid,
                (int)info.InheritedFromUniqueProcessId,
                ReadName(info.ImageName, pid),
                info.CreateTime,
                info.KernelTime + info.UserTime,
                (long)(ulong)info.WorkingSetSize,
                (ulong)(info.ReadTransferCount + info.WriteTransferCount)));

            if (info.NextEntryOffset == 0)
            {
                return entries;
            }

            offset += (int)info.NextEntryOffset;
        }
    }

    // Mesmo formato do Process.ProcessName do .NET (sem caminho e sem ".exe"), pra não mudar
    // os nomes já gravados nos snapshots antigos nem quebrar os padrões de exclusão do usuário.
    private static string ReadName(UnicodeString imageName, int pid)
    {
        if (imageName.Buffer == IntPtr.Zero || imageName.Length == 0)
        {
            return pid switch { 0 => "Idle", 4 => "System", _ => string.Empty };
        }

        var name = Marshal.PtrToStringUni(imageName.Buffer, imageName.Length / 2);
        var lastSeparator = name.LastIndexOf('\\');
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // Layout de SYSTEM_PROCESS_INFORMATION (winternl.h/phnt). Campos de tamanho de ponteiro
    // como IntPtr/UIntPtr pra o alinhamento bater com o C nativo. Seguido na memória por
    // NumberOfThreads entradas de thread, que aqui não interessam (NextEntryOffset pula).
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}
