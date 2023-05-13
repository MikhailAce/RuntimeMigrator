using System.Collections.Generic;

namespace RuntimeMigrator.Models
{
	public class CustomMigration
	{
		public IEnumerable<string> UpSqlCommands { get; set; }
		public IEnumerable<string> DownSqlCommands { get; set; }
	}
}
