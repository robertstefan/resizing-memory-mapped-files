using System;

namespace MemoryMappedFiles
{
	public struct Range
	{
		public long Start { get; }
		public long End { get; }

		public Range(long alpha, long omega)
		{
			Start = alpha;
			End = omega;
		}

		public override string ToString()
		{
			return $"{Start} ===> {End};";
		}
	}
}
