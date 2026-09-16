using Avalonia.Data.Converters;
using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Desktop;

/// <summary>送る前の添付に出す大きさ（設計 §58-6）。</summary>
public static class AttachmentSize
{
    public static readonly IValueConverter Converter =
        new FuncValueConverter<long, string>(bytes => Attachments.Megabytes(bytes));
}
