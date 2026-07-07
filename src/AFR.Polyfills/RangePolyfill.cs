// Polyfill: Range/Index 切片语法（text[start..end]）需要这些类型（C# 8+，仅 .NET Standard 2.1+ 内置）
// ReSharper disable once CheckNamespace
namespace System
{
    internal readonly struct Index(int value, bool fromEnd = false) : IEquatable<Index>
    {
        private readonly int _value = fromEnd ? ~value : value;

        public static Index Start => new(0);
        public static Index End => new(~0);

        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(~value);

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public static implicit operator Index(int value) => new(value);

        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;
        public override string ToString() => IsFromEnd ? $"^{Value}" : Value.ToString();
    }

    internal readonly struct Range(Index start, Index end) : IEquatable<Range>
    {
        public Index Start { get; } = start;
        public Index End { get; } = end;

        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => $"{Start}..{End}";
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class RuntimeHelpers
    {
        [ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)]
        public static T[] GetSubArray<T>(T[] array, Range range)
        {
            var (offset, length) = range.GetOffsetAndLength(array.Length);
            var dest = new T[length];
            Array.Copy(array, offset, dest, 0, length);
            return dest;
        }
    }
}
