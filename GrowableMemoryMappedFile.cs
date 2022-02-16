using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryMappedFiles
{
	public interface IExportableWrittenZones
	{
		void ExportWritenZones();

		void LoadWrittenZonesMeta(string file);
	}

	public sealed unsafe class GrowableMemoryMappedFile : IExportableWrittenZones, IDisposable
	{
		private const int AllocationGranularity = 64 * 1024;
		private const sbyte WriteDelimiterZone = sbyte.MaxValue;

		private class MemoryMappedArea
		{
			public MemoryMappedFile? Mmf;
			public byte* Address;
			public long Size;
		}

		private FileStream fs;

		private List<MemoryMappedArea> areas = new List<MemoryMappedArea>();
		private long[] offsets = new long[] { 0 };
		private byte*[]? addresses;

		public long Length
		{
			get
			{
				CheckDisposed();
				return fs.Length;
			}
		}

		private string _memoryFileName = Path.GetTempFileName();
		public void SetMemoryName(string memoryFileName)
		{
			_memoryFileName = memoryFileName;
		}

		public string GetMemoryName() { return _memoryFileName; }

		public GrowableMemoryMappedFile(string filePath, long initialFileSize)
		{
			if (initialFileSize <= 0 || initialFileSize % AllocationGranularity != 0)
			{
				throw new ArgumentException("The initial file size must be a multiple of 64Kb and grater than zero", nameof(initialFileSize));
			}

			bool existingFile = File.Exists(filePath);

			fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

			if (existingFile)
			{
				if (fs.Length <= 0 || fs.Length % AllocationGranularity != 0)
				{
					throw new ArgumentException(paramName: nameof(filePath), message: "Invalid file. Its lenght must be a multiple of 64Kb and greater than zero");
				}
			}
			else
			{
				fs.SetLength(initialFileSize);
			}

			CreateFirstArea();
		}

		private void CreateFirstArea()
		{
			MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(fs,
													  _memoryFileName,
													  fs.Length,
													  MemoryMappedFileAccess.ReadWrite,
													  HandleInheritability.Inheritable,
													  true);

			byte* address = Win32FileMapping.MapViewOfFileEx(mmf.SafeMemoryMappedFileHandle.DangerousGetHandle(),
				Win32FileMapping.FileMapAccess.Read | Win32FileMapping.FileMapAccess.Write,
				0, 0, new UIntPtr((ulong)fs.Length), null);

			if (address == null)
				throw new Win32Exception();

			MemoryMappedArea area = new MemoryMappedArea
			{
				Address = address,
				Mmf = mmf,
				Size = fs.Length
			};
			areas.Add(area);

			addresses = new byte*[] { address };

		}

		public void Grow(long bytesToGrow)
		{
			CheckDisposed();
			if (bytesToGrow <= 0 || bytesToGrow % AllocationGranularity != 0)
			{
				throw new ArgumentException(paramName: nameof(bytesToGrow), message: "The growth must be a multiple of 64Kb and greater than zero");
			}

			long offset = fs.Length;
			fs.SetLength(fs.Length + bytesToGrow);

			MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(fs,
																   _memoryFileName,
																   fs.Length,
																   MemoryMappedFileAccess.ReadWrite,
																   HandleInheritability.Inheritable,
																   true);

			uint* offsetPointer = (uint*)&offset;
			MemoryMappedArea lastArea = areas[areas.Count - 1];
			byte* desiredAddress = lastArea.Address + lastArea.Size;
			byte* address = Win32FileMapping.MapViewOfFileEx(mmf.SafeMemoryMappedFileHandle.DangerousGetHandle(),
				Win32FileMapping.FileMapAccess.Read | Win32FileMapping.FileMapAccess.Write,
				offsetPointer[1], offsetPointer[0], new UIntPtr((ulong)bytesToGrow), desiredAddress);

			if (address == null)
			{
				address = Win32FileMapping.MapViewOfFileEx(mmf.SafeMemoryMappedFileHandle.DangerousGetHandle(),
				   Win32FileMapping.FileMapAccess.Read | Win32FileMapping.FileMapAccess.Write,
				   offsetPointer[1], offsetPointer[0], new UIntPtr((ulong)bytesToGrow), null);
			}

			if (address == null)
				throw new Win32Exception();

			MemoryMappedArea area = new MemoryMappedArea
			{
				Address = address,
				Mmf = mmf,
				Size = bytesToGrow
			};

			areas.Add(area);

			if (desiredAddress != address)
			{
				offsets = offsets.Add(offset);
				addresses = addresses?.Add(address);
			}
		}

		public byte* GetPointer(long offset)
		{
			CheckDisposed();
			int i = offsets.Length;
			if (i <= 128) // linear search is more efficient for small arrays. Experiments show 140 as the cutpoint on x64 and 100 on x86.
			{
				while (--i > 0 && offsets[i] > offset)
				{ }
			}
			else // binary search is more efficient for large arrays
			{
				i = Array.BinarySearch<long>(offsets, offset);
				if (i < 0) i = ~i - 1;
			}
			return addresses![i] + offset - offsets[i];
		}

		private bool isDisposed;

		public void Dispose()
		{
			if (isDisposed) return;
			isDisposed = true;
			foreach (MemoryMappedArea a in this.areas)
			{
				Win32FileMapping.UnmapViewOfFile(a.Address);
				a.Mmf?.Dispose();
			}
			fs.Dispose();
			areas.Clear();
		}

		private void CheckDisposed()
		{
			if (isDisposed) throw new ObjectDisposedException(this.GetType().Name);
		}

		public void Flush()
		{
			CheckDisposed();
			foreach (MemoryMappedArea area in areas)
			{
				if (!Win32FileMapping.FlushViewOfFile(area.Address, new IntPtr(area.Size)))
				{
					throw new Win32Exception();
				}
			}
			fs.Flush(true);
		}

		private List<Range> streamRanges = new List<Range>();

		public void WriteData(byte[] data)
		{
			var mmf = areas.LastOrDefault()?.Mmf;
			{
				long startPos = streamRanges.Count > 0 ? streamRanges.LastOrDefault().End + WriteDelimiterZone : 0;

				using (var accessor = mmf?.CreateViewAccessor())
				{
					accessor?.WriteArray(startPos, data, 0, data.Length);

					streamRanges.Add(new Range(startPos, startPos + data.Length));

					byte[] debugData;
					ReadData(streamRanges.LastOrDefault(), out debugData);

					if (!debugData.SequenceEqual(data))
					{
						throw new IOException("Data was not written");
					}

					accessor?.Flush();
				}
			}
		}

		public void ReadData(Range readRange, out byte[] buffer)
		{
			CheckDisposed();
			var mmf = areas.LastOrDefault()?.Mmf;

			buffer = new byte[readRange.End - readRange.Start];

			using (var accessor = mmf?.CreateViewAccessor())
			{
				accessor?.ReadArray(readRange.Start, buffer, 0, buffer.Length);
			}
		}

		public void Clear()
		{
			CheckDisposed();
			foreach (MemoryMappedArea area in areas.Skip(1))
			{
				Win32FileMapping.UnmapViewOfFile(area.Address);
				area?.Mmf?.Dispose();
			}

			fs.Position = 0;
			fs.SetLength(0);
			areas.RemoveRange(1, areas.Count - 1);
		}

		public void ExportWritenZones()
		{
			StreamWriter sW = new StreamWriter($"{_memoryFileName}.meta-zones");

			foreach (Range range in streamRanges)
			{
				sW.WriteLine(range.ToString());
			}

			sW.Flush();
			sW.Close();
		}

		public void LoadWrittenZonesMeta(string file)
		{
			if (!Path.GetExtension(file).EndsWith("meta-zones"))
			{
				throw new ArgumentException("Invalid file extension.", nameof(file));
			}

			StreamReader sR = new StreamReader(file);
			Regex pattern = new Regex("(?<start>.*) ={3}> (?<end>.*);", RegexOptions.Singleline | RegexOptions.Compiled);

			long start, end;

			while (!sR.EndOfStream)
			{
				Match data = pattern.Match(sR.ReadLine());
				
				start = long.Parse(data.Groups["start"].Value);
				end = long.Parse(data.Groups["end"].Value);

				streamRanges.Add(new Range(start, end));			
			}

			sR.Close();
		}
	}
}
