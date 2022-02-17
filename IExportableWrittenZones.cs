namespace MemoryMappedFiles
{
	public interface IExportableWrittenZones
	{
		void ExportWrittenZones(out string metaFile);

		void LoadWrittenZonesMeta(string metaFile);
	}
}
