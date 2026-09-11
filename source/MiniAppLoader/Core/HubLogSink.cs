using System;
using System.Collections.ObjectModel;
using System.Threading;
using Serilog.Core;
using Serilog.Events;

namespace MiniAppLoader.Core;

/// <summary>Một dòng log hiển thị trong khung log của Plugin Hub.</summary>
public sealed record HubLogEntry(DateTime Timestamp, string Level, string Message);

/// <summary>
///     Sink Serilog đổ log vào thẳng Plugin Hub, thay cho việc bắn <c>TaskDialog</c> mỗi
///     lần có chuyện như bản v1.
///     <para>
///         Serilog gọi <see cref="Emit"/> từ thread bất kỳ (đặc biệt là thread nền của
///         FileSystemWatcher), nên mọi thao tác lên collection đều phải đi qua
///         <see cref="SynchronizationContext"/> bắt được lúc dựng pane — tức là UI thread.
///     </para>
/// </summary>
public sealed class HubLogSink : ILogEventSink
{
    private const int MaxEntries = 500;

    private readonly ObservableCollection<HubLogEntry> _entries = [];
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public ReadOnlyObservableCollection<HubLogEntry> Entries { get; }

    public HubLogSink() => Entries = new ReadOnlyObservableCollection<HubLogEntry>(_entries);

    public void Emit(LogEvent logEvent)
    {
        var entry = new HubLogEntry(
            logEvent.Timestamp.LocalDateTime,
            logEvent.Level.ToString().Substring(0, 3).ToUpperInvariant(),
            logEvent.RenderMessage());

        if (_uiContext is null) Append(entry);
        else _uiContext.Post(_ => Append(entry), null);
    }

    public void Clear() => _entries.Clear();

    private void Append(HubLogEntry entry)
    {
        _entries.Add(entry);

        // Giữ khung log gọn: cắt bớt từ đầu thay vì để phình vô hạn suốt phiên Revit.
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }
}
