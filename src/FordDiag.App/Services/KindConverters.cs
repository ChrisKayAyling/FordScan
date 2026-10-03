using System.Globalization;
using Avalonia.Data.Converters;

namespace FordDiag.App.Services;

public static class KindConverters
{
    public static IValueConverter IsError { get; } = new Kind(LogKind.Error);
    public static IValueConverter IsTx { get; } = new Kind(LogKind.Tx);
    public static IValueConverter IsDim { get; } = new Kind(LogKind.Trace);

    private sealed class Kind(LogKind k) : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is LogKind x && x == k;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }
}
