using System.ComponentModel.DataAnnotations.Schema;

namespace RuntimeMigrator.Models
{
	public class EntityModelSnapshot
	{
		[Column("snapshotcode")]
		public byte[] SnapshotCode { get; set; }
	}
}
