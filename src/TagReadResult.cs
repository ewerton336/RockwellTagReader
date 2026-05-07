namespace RockwellTagReader;

/// <summary>
/// Materialized result of a single CIP Read Tag (single or MSP sub-reply):
/// raw status/typeCode/bytes plus the decoded string value, ready for consumers
/// that don't want to know about CIP wire format.
/// </summary>
public sealed record TagReadResult(
    string TagName,
    byte GeneralStatus,
    ushort? TypeCode,
    byte[]? ValueBytes,
    int? BitNumber,
    string? DecodedValue,
    string? ErrorMessage)
{
    /// <summary>True when the read returned a successfully decoded value.</summary>
    public bool IsSuccess => GeneralStatus == 0 && DecodedValue is not null;

    /// <summary>
    /// Builds a <see cref="TagReadResult"/> from a raw MSP entry. Decodes the value
    /// (or applies bit masking if the original tag used <c>tag.N</c> syntax).
    /// </summary>
    public static TagReadResult From(MspRawResult raw)
    {
        var plan = LogixTagPath.Plan(raw.TagName);

        if (raw.GeneralStatus != 0 || raw.TypeCode is null || raw.ValueBytes is null)
        {
            return new TagReadResult(
                TagName: raw.TagName,
                GeneralStatus: raw.GeneralStatus,
                TypeCode: raw.TypeCode,
                ValueBytes: raw.ValueBytes,
                BitNumber: plan.BitNumber,
                DecodedValue: null,
                ErrorMessage: raw.ErrorMessage);
        }

        return From(raw.TagName, plan, raw.TypeCode.Value, raw.ValueBytes);
    }

    internal static TagReadResult From(string tagName, LogixTagPlan plan, MspSubResult sub)
    {
        if (sub.GeneralStatus != 0 || sub.TypeCode is null || sub.ValueBytes is null)
        {
            return new TagReadResult(
                TagName: tagName,
                GeneralStatus: sub.GeneralStatus,
                TypeCode: sub.TypeCode,
                ValueBytes: sub.ValueBytes,
                BitNumber: plan.BitNumber,
                DecodedValue: null,
                ErrorMessage: sub.ErrorMessage);
        }

        return From(tagName, plan, sub.TypeCode.Value, sub.ValueBytes);
    }

    internal static TagReadResult From(string tagName, LogixTagPlan plan, ushort typeCode, byte[] valueBytes)
    {
        try
        {
            var decoded = plan.BitNumber.HasValue
                ? CipReadTag.DecodeBit(typeCode, valueBytes, plan.BitNumber.Value)
                : CipReadTag.DecodeValue(typeCode, valueBytes);

            return new TagReadResult(
                TagName: tagName,
                GeneralStatus: 0,
                TypeCode: typeCode,
                ValueBytes: valueBytes,
                BitNumber: plan.BitNumber,
                DecodedValue: decoded,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new TagReadResult(
                TagName: tagName,
                GeneralStatus: 0,
                TypeCode: typeCode,
                ValueBytes: valueBytes,
                BitNumber: plan.BitNumber,
                DecodedValue: null,
                ErrorMessage: $"decode error: {ex.Message}");
        }
    }
}
