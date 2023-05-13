using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NLog;
using Npgsql;
using RuntimeMigrator.Models;

namespace RuntimeMigrator
{
	/// <summary>
	/// Класс инкапсулирует функционал миграций
	/// </summary>
	public class MigrationService : IOperationReporter
	{
		#region Private fields

		private readonly Logger _logger;
		private readonly DbContext _context;
		private readonly IServiceProvider _dbServices;
		private readonly MigrationsScaffolderDependencies _migrationsScaffolderDependencies;
		private readonly string _connectionString = "";

		#endregion

		#region .ctor

		public MigrationService(DbContext context)
		{
			_logger = LogManager.GetCurrentClassLogger();
			_context = context;
			_dbServices = GetDbServices();
			_migrationsScaffolderDependencies = _dbServices.GetRequiredService<MigrationsScaffolderDependencies>();
		}

		#endregion

		#region Public methods

		/// <summary>
		/// Создание новой миграции на основе различий между текущим снимком модели БД и структуры моделей данных.
		/// </summary>
		public async Task<StepResultBase> AddMigration()
		{
			StepResultBase result;

			try
			{
				// Получаем список неприменных миграций
				List<string> notAppliedMigrations = await GetNotAppliedMigrationIds();

				// Если есть непримененные миграции, то отменяем создание новой миграции
				if (notAppliedMigrations == null || !notAppliedMigrations.Any())
				{
					// Миграция является инициализирующей, если на данный момент нет снимка или список задекларированных миграций пуст
					List<string> declaredMigrationIds = GetDeclaredMigrationIds();
					string modelSnapshot = await GetCurrentModelSnapshotCode();
					bool isInitMigration = !declaredMigrationIds.Any() && string.IsNullOrEmpty(modelSnapshot);

					// Генерируем идентификатор новой миграции
					string migrationId = GenerateMigrationId(isInitMigration);

					// Генерируем миграцию
					CustomMigration migration = await GenerateMigration(migrationId);

					// Если миграция сгенерирована и содержит скрипты обновления
					if (migration != null
						&& migration.UpSqlCommands.Any()
						&& migration.DownSqlCommands.Any())
					{
						// Формируем и сохраняем файл миграции в формате json
						string migrationFileName = $"{migrationId}.json";
						string migrationFilePath = Path.Combine("", migrationFileName);

						string jsonObject = JsonConvert.SerializeObject(migration, Formatting.Indented);

						File.WriteAllText(migrationFilePath, jsonObject);

						_logger.Debug($"Миграция {migrationFileName} успешно создана. Файл миграции сохранен по пути: {migrationFilePath}.");
						result = new StepResult<string>($"Миграция {migrationFileName} успешно создана.", true, migrationId);
					}
					else
					{
						_logger.Debug($"Модель данных соответствует структуре БД. Миграция не создана.");
						result = new StepResult<string>($"Модель данных соответствует структуре БД. Миграция не создана.", false, null);
					}
				}
				else
				{
					_logger.Debug("Есть непримененные миграции. Миграция не создана.");
					result = new StepResult<string>("Есть непримененные миграции. Миграция не создана.", false, null);
				}
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "Произошла ошибка в процессе создания миграции.");
				result = new StepResult<string>("Произошла ошибка в процессе создания миграции.", false, null);
			}

			return result;
		}

		public async Task<StepResultBase> SaveMigrations(IEnumerable<string> migrationIds = null)
		{
			StepResultBase result = null;
			StepResult<List<string>> migrationsToApplyResult = await GetMigrationsToApply(migrationIds);

			if (migrationsToApplyResult.Success)
			{
				List<string> migrationsToApply = migrationsToApplyResult.Data;

				if (migrationsToApply.Any())
				{
					// Добавляем новый снимок модели БД в таблицу "modelsnapshot"
					await SaveModelSnapshot(migrationsToApply.Last());

					// Проверяем наличие и при необходимости создаем таблицу истории "__EFMigrationsHistory"
					await EnsureCreateEFHistoryTable();

					// Добавляем в таблицу "__EFMigrationsHistory" примененные миграции
					foreach (string migrationId in migrationsToApply)
						await AddEFHistoryRecord(migrationId);

					string message = migrationsToApply.Count() == 1
						? $"Миграция {migrationsToApply.FirstOrDefault()} успешно сохранена."
						: $"Миграции {string.Join(", ", migrationsToApply)} успешно сохранены.";

					_logger.Debug(message);
					result = new StepResultBase(message, true);
				}
				else
				{
					_logger.Debug("Нет задекларированных миграций для сохранения.");
					result = new StepResultBase("Отсутствуют миграции для сохранения.", false);
				}
			}
			else
			{
				_logger.Debug(migrationsToApplyResult.Message);
				result = new StepResultBase(migrationsToApplyResult.Message, false);
			}

			return result;
		}

		/// <summary>
		/// Обновление БД путем применения новых задекларированных миграций.
		/// Получение списка задекларированных непримененных миграций, формирование скрипта для обновления БД на основе блоков "up", обновление БД.
		/// </summary>
		public async Task<StepResultBase> ApplyMigrations(IEnumerable<string> migrationIds = null)
		{
			StepResultBase result = new StepResultBase(true);

			try
			{
				// Получаем список миграций для применения
				StepResult<List<string>> migrationsToApplyResult = await GetMigrationsToApply(migrationIds);

				if (migrationsToApplyResult.Success)
				{
					List<string> migrationsToApply = migrationsToApplyResult.Data;

					if (migrationsToApply != null && migrationsToApply.Any())
					{
						// Получаем актуальный снимок модели БД
						string modelSnapshotCode = await GetCurrentModelSnapshotCode();
						List<string> appliedMigrationIds = await GetAppliedMigrationIds();

						// Если еще нет снимка модели или нет примененных миграций,
						// и пользователь пытается применить сразу несколько миграций,
						// то возвращаем ошибку
						if ((string.IsNullOrWhiteSpace(modelSnapshotCode) || appliedMigrationIds.Count == 0)
							&& migrationsToApply.Count > 1)
						{
							_logger.Debug("Необходимо сначала создать и применить инициализирующую миграцию.");
							result = new StepResultBase($"Необходимо сначала создать и применить инициализирующую миграцию.", false);
						}
						// Если еще нет снимка модели или нет примененных миграций,
						// и пользователь пытается применить одну миграцию
						// то считаем что пользователь пытается применить инициализирующую миграцию
						else if ((string.IsNullOrWhiteSpace(modelSnapshotCode) || appliedMigrationIds.Count == 0)
							&& migrationsToApply.Count == 1)
						{
							// Если БД еще не создана (отсутствуют любые таблицы)
							if (!await DatabaseExists())
							{
								result = await UpdateDatabase(migrationsToApply);
							}
							else
							{
								string migrationId = migrationsToApply.First();
								result = await SaveMigrations(new string[] { migrationId });
							}
						}
						else
						{
							result = await UpdateDatabase(migrationsToApply);
						}
					}
					else
					{
						_logger.Debug("Нет задекларированных миграций для применения. БД не обновлена.");
						result = new StepResultBase("Нет задекларированных миграций для применения. БД не обновлена.", false);
					}
				}
				else
				{
					_logger.Debug(migrationsToApplyResult.Message);
					result = new StepResultBase(migrationsToApplyResult.Message, false);
				}
			}
			catch (PostgresException ex)
			{
				_logger.Error(ex, "Произошла ошибка в процессе применения миграций и обновления БД.");
				result = new StepResultBase($"Произошла ошибка в процессе применения миграций и обновления БД. {ex.Message} {ex.Hint}", false);
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "Произошла ошибка в процессе применения миграций и обновления БД.");
				result = new StepResultBase($"Произошла ошибка в процессе применения миграций и обновления БД. {ex.Message}", false);
			}

			return result;
		}

		/// <summary>
		/// Откат версии БД до целевой миграции.
		/// Получение списка миграций для отката, формирование скрипта для обновления БД на основе блоков "down", обновление БД.
		/// </summary>
		/// <param name="targetMigrationId">Целевая миграция, до которой требуется откатить версию БД</param>
		public async Task<StepResultBase> RollbackMigrations(string targetMigrationId)
		{
			StepResultBase result = new StepResultBase(true);

			// Перед восстановлением версии БД проводим необходимые проверки
			StepResultBase validateRollbackResult = await ValidateRollback(targetMigrationId);

			if (validateRollbackResult.Success)
			{
				try
				{
					// Получаем список примененных к БД миграций
					List<string> appliedMigrationIds = await GetAppliedMigrationIds();

					// Формируем список миграций для отката
					int targetMigrationIndex = appliedMigrationIds.IndexOf(targetMigrationId);
					IEnumerable<string> rollbackMigrationIds = appliedMigrationIds.Skip(targetMigrationIndex + 1);

					StringBuilder updateSqlScript = new StringBuilder();

					// Получаем актуальный снимок модели БД
					string modelSnapshotCode = await GetCurrentModelSnapshotCode();

					// Формируем скрипт обновления БД в обратном порядке следования миграций
					foreach (var migrationId in rollbackMigrationIds.Reverse())
					{
						// Получаем миграцию по идентификатору
						StepResult<CustomMigration> migrationResult = GetDeclaredMigration(migrationId);

						if (migrationResult.Success)
						{
							// Получаем команды блока "down" и добавляем их в общий скрипт
							string downOperationsScript = MigrationUtils.SqlCommandsToScript(migrationResult.Data.DownSqlCommands);
							updateSqlScript.AppendLine(downOperationsScript);
						}
						else
						{
							_logger.Debug($"Произошла ошибка в процессе восстановления версии БД. {migrationResult.Message}.");
							result = new StepResultBase($"Произошла ошибка в процессе восстановления версии БД. {migrationResult.Message}.", false);

							// В случае возникновения ошибки, выходим из цикла
							break;
						}
					}

					if (result.Success)
					{
						// Выполняем скрипт обновления БД
						await ExecuteSql(updateSqlScript.ToString());

						// Удаляем из таблицы "__EFMigrationsHistory" отмененные миграции
						foreach (var migrationId in rollbackMigrationIds)
							await DeleteEFHistoryRecord(migrationId);

						// Добавляем новый снимок модели БД в таблицу "modelsnapshot"
						await SaveModelSnapshot(targetMigrationId);

						// Удаляем файлы отмененных миграций из проектной директории
						StepResultBase deletingResult = DeleteDeclaredMigrations(rollbackMigrationIds);

						if (deletingResult.Success)
						{
							_logger.Debug($"Обновление БД до миграции {targetMigrationId} проведено успешно.");
							result = new StepResultBase($"Обновление БД до миграции {targetMigrationId} проведено успешно.", true);
						}
						else
						{
							_logger.Debug(deletingResult.Message);
							result = new StepResultBase(deletingResult.Message, false);
						}
					}
				}
				catch (PostgresException ex)
				{
					_logger.Error(ex, "Произошла ошибка в процессе восстановления версии БД.");
					result = new StepResultBase($"Произошла ошибка в процессе восстановления версии БД. {ex.Message} {ex.Hint}", false);
				}
				catch (Exception ex)
				{
					_logger.Error(ex, "Произошла ошибка в процессе восстановления версии БД.");
					result = new StepResultBase($"Произошла ошибка в процессе восстановления версии БД. {ex.Message}", false);
				}
			}
			else
			{
				_logger.Debug($"Произошла ошибка в процессе восстановления версии БД. {validateRollbackResult.Message}");
				result = validateRollbackResult;
			}

			return result;
		}

		/// <summary>
		/// Удаление указанного списка задекларированных миграций.
		/// При удалении задекларированных миграций НЕ происходит автоматическое удаление применных миграций.
		/// </summary>
		/// <param name="migrationIds">Список задекларированных миграций</param>
		public StepResultBase DeleteDeclaredMigrations(IEnumerable<string> migrationIds)
		{
			if (migrationIds != null && migrationIds.Count() > 0)
			{
				foreach (string migrationId in migrationIds)
				{
					try
					{
						// Формируем путь до файла миграции
						string filePath = Path.Combine("", migrationId + ".json");

						// Удаляем файл миграции
						if (File.Exists(filePath))
						{
							File.Delete(filePath);
						}
					}
					catch (Exception ex)
					{
						_logger.Error(ex, $"Не удалось удалить файл миграции {migrationId}.");
						return new StepResultBase($"Не удалось удалить файл миграции {migrationId}. {ex.Message}", false);
					}
				}
			}

			return new StepResultBase(true);
		}

		/// <summary>
		/// Получение списка непримененных миграций.
		/// </summary>
		public async Task<List<string>> GetNotAppliedMigrationIds()
		{
			List<string> notAppliedMigrationIds = new List<string>();
			// Получаем список задекларированных миграций
			List<string> declaredMigrationIds = GetDeclaredMigrationIds();
			// Получаем список примененных к БД миграций
			List<string> appliedMigrationIds = await GetAppliedMigrationIds();

			if (declaredMigrationIds != null && declaredMigrationIds.Any())
			{
				// Из списка задекларированных миграций вычитаем список примененных к БД миграций
				notAppliedMigrationIds = declaredMigrationIds
					.Except(appliedMigrationIds)
					.OrderBy(x => x)
					.ToList();
			}

			return notAppliedMigrationIds;
		}

		/// <summary>
		/// Получить список задекларированных миграций.
		/// </summary>
		public List<string> GetDeclaredMigrationIds()
		{
			List<string> declaredMigrationIds = new List<string>();

			string[] migrationFileNames = Directory.GetFiles("");
			if (migrationFileNames != null && migrationFileNames.Any())
			{
				declaredMigrationIds = migrationFileNames
					.OrderBy(x => x)
					.Select(x => Path.GetFileNameWithoutExtension(x))
					.ToList();
			}

			return declaredMigrationIds;
		}

		/// <summary>
		/// Получить список примененных к БД миграций.
		/// </summary>
		public async Task<List<string>> GetAppliedMigrationIds()
		{
			List<string> appliedMigrationIds = new List<string>();

			try
			{
				using (var connection = new NpgsqlConnection(_connectionString))
				{
					if (connection.State == ConnectionState.Closed)
						connection.Open();

					bool tableExists = false;

					// Проверяем ниличие в БД таблицы "__EFMigrationsHistory"
					using (NpgsqlCommand tableExistsCommand = connection.CreateCommand())
					{
						tableExistsCommand.CommandText = $"SELECT * FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory';";
						using (NpgsqlDataReader reader = await tableExistsCommand.ExecuteReaderAsync())
						{
							if (reader.HasRows)
								tableExists = true;
						}
					}

					if (tableExists)
					{
						// Получаем список всех применных миграций
						using (NpgsqlCommand migrationIdsCommand = connection.CreateCommand())
						{
							migrationIdsCommand.CommandText =
								"SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\";";

							using (NpgsqlDataReader reader = migrationIdsCommand.ExecuteReader())
							{
								while (reader.Read())
								{
									appliedMigrationIds.Add(reader.GetString(0));
								}
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "Не удалось получить список примененных миграций.");
			}

			return appliedMigrationIds;
		}

		/// <summary>
		/// Предварительная валидация процесса восстановления версии БД.
		/// </summary>
		/// <param name="targetMigrationId">Идентификатор целевой миграции</param>
		public async Task<StepResultBase> ValidateRollback(string targetMigrationId)
		{
			if (string.IsNullOrWhiteSpace(targetMigrationId))
			{
				return new StepResultBase("Необходимо указать целевую миграцию для обновления. Обновление не выполнено.", false);
			}

			targetMigrationId = Path.GetFileNameWithoutExtension(targetMigrationId);

			// Получаем список примененных к БД миграций
			List<string> appliedMigrationIds = await GetAppliedMigrationIds();

			if (appliedMigrationIds == null || appliedMigrationIds.Count == 0)
			{
				return new StepResultBase("На данный момент нет примененных миграций. Обновление не выполнено.", false);
			}

			if (!appliedMigrationIds.Contains(targetMigrationId))
			{
				return new StepResultBase($"Не удалось найти миграцию с идентификатором \"{targetMigrationId}\". Обновление не выполнено.", false);
			}

			if (string.Equals(appliedMigrationIds.Last(), targetMigrationId, StringComparison.OrdinalIgnoreCase))
			{
				return new StepResultBase("Целевая миграция является последней. Обновление не выполнено.", false);
			}

			// Формируем список миграций для отката версии БД
			int targetMigrationIndex = appliedMigrationIds.IndexOf(targetMigrationId);
			IEnumerable<string> rollbackMigrationIds = appliedMigrationIds.Skip(targetMigrationIndex + 1);

			if (rollbackMigrationIds == null || rollbackMigrationIds.Count() == 0)
			{
				return new StepResultBase("Не удалось сформировать список миграций для отката. Обновление не выполнено.", false);
			}

			// Получаем актуальный снимок модели БД
			string modelSnapshotCode = await GetCurrentModelSnapshotCode();

			if (string.IsNullOrWhiteSpace(modelSnapshotCode))
			{
				return new StepResultBase("Не удалось получить актуальный снимок модели БД. Обновление не выполнено.", false);
			}

			return new StepResultBase(true);
		}

		public void WriteError(string message)
		{
			_logger.Error(message);
		}

		public void WriteInformation(string message)
		{
			_logger.Info(message);
		}

		public void WriteVerbose(string message)
		{
			_logger.Info(message);
		}

		public void WriteWarning(string message)
		{
			_logger.Warn(message);
		}

		#endregion

		#region Private methods

		private IServiceProvider GetDbServices()
		{
			IMigrationsAssembly migrationAssembly = _context.GetService<IMigrationsAssembly>();
			DesignTimeServicesBuilder builder = new DesignTimeServicesBuilder(migrationAssembly.Assembly, Assembly.GetEntryAssembly(), this, null);

			return builder.Build(_context);
		}

		/// <summary>
		/// Получить актуальный снимок модели БД.
		/// </summary>
		/// <returns></returns>
		private async Task<string> GetCurrentModelSnapshotCode()
		{
			string snapshotCodeSource = null;

			if (await TableExists("modelsnapshot"))
			{
				if (_context.Set<EntityModelSnapshot>().Any())
				{
					EntityModelSnapshot modelSnapshot = _context.Set<EntityModelSnapshot>()
						.FirstOrDefault();

					if (modelSnapshot != null)
					{
						snapshotCodeSource = await MigrationUtils.DecompressSource(modelSnapshot.SnapshotCode);
					}
				}
			}

			return snapshotCodeSource;
		}

		private async Task<bool> DatabaseExists()
		{
			bool result = false;

			using (var connection = new NpgsqlConnection(_connectionString))
			{
				if (connection.State == ConnectionState.Closed)
					connection.Open();

				bool tableExists = false;

				// Проверяем ниличие в БД таблицы "__EFMigrationsHistory"
				using (NpgsqlCommand tableExistsCommand = connection.CreateCommand())
				{
					tableExistsCommand.CommandText = $"SELECT * FROM information_schema.tables where table_schema = 'public';";
					using (NpgsqlDataReader reader = await tableExistsCommand.ExecuteReaderAsync())
					{
						if (reader.HasRows)
							result = true;
					}
				}
			}

			return result;
		}

		private async Task<bool> TableExists(string tableName)
		{
			bool result = false;

			using (var connection = new NpgsqlConnection(_connectionString))
			{
				if (connection.State == ConnectionState.Closed)
					connection.Open();

				bool tableExists = false;

				// Проверяем ниличие в БД таблицы "__EFMigrationsHistory"
				using (NpgsqlCommand tableExistsCommand = connection.CreateCommand())
				{
					tableExistsCommand.CommandText = $"SELECT * FROM information_schema.tables WHERE table_name = '{tableName}';";
					using (NpgsqlDataReader reader = await tableExistsCommand.ExecuteReaderAsync())
					{
						if (reader.HasRows)
							result = true;
					}
				}
			}

			return result;
		}

		/// <summary>
		/// Проверить наличие в БД таблицы __EFMigrationsHistory и при необходимости создать ее
		/// </summary>
		private async Task EnsureCreateEFHistoryTable()
		{
			if (!_migrationsScaffolderDependencies.HistoryRepository.Exists())
			{
				string createScript = _migrationsScaffolderDependencies.HistoryRepository.GetCreateScript();

				await ExecuteSql(createScript);
			}
		}

		/// <summary>
		/// Добавить в таблицу "__EFMigrationsHistory" новую запись
		/// </summary>
		/// <param name="migrationId">Наименование миграции</param>
		private async Task AddEFHistoryRecord(string migrationId)
		{
			HistoryRow historyRow = new HistoryRow(
				migrationId,
				typeof(DbContext).Assembly.GetName().Version.ToString()
			);

			var sqlCommand = _migrationsScaffolderDependencies
				.HistoryRepository
				.GetInsertScript(historyRow);

			await ExecuteSql(sqlCommand);
		}

		/// <summary>
		/// Удалить запись из таблицы истории "__EFMigrationsHistory".
		/// </summary>
		/// <param name="migrationId">Идентификатор целевой миграции</param>
		private async Task DeleteEFHistoryRecord(string migrationId)
		{
			var sqlCommand = _migrationsScaffolderDependencies
				.HistoryRepository
				.GetDeleteScript(migrationId);

			await ExecuteSql(sqlCommand);
		}

		/// <summary>
		/// Получить объектное представление миграции.
		/// </summary>
		/// <param name="migrationId">Идентификатор целевой миграции</param>
		private StepResult<CustomMigration> GetDeclaredMigration(string migrationId)
		{
			StepResult<CustomMigration> result;

			string migrationPath = Path.Combine("", $"{migrationId}.json");

			if (File.Exists(migrationPath))
			{
				try
				{
					result = new StepResult<CustomMigration>("", true, JsonConvert.DeserializeObject<CustomMigration>(File.ReadAllText(migrationPath)));
				}
				catch (Exception ex)
				{
					result = new StepResult<CustomMigration>($"Не удалось прочитать файл миграции {migrationId}. {ex.Message}", false, null);
				}
			}
			else
			{
				result = new StepResult<CustomMigration>($"Не удалось найти файл миграции {migrationId}.", false, null);
			}

			return result;
		}

		/// <summary>
		/// Сгенерировать и сохранить новый снимок модели БД.
		/// </summary>
		/// <param name="migrationId">Идентификатор целевой миграции</param>
		/// <returns></returns>
		private async Task SaveModelSnapshot(string migrationId)
		{
			IMigrationsCodeGenerator codeGenerator = _migrationsScaffolderDependencies
				.MigrationsCodeGeneratorSelector
				.Select(null);

			try
			{
				string modelSnapshotName = $"ModelSnapshot_{migrationId}";

				// Генерируем новый снимок модели БД на основе текущего контекста БД
				string modelSource = codeGenerator.GenerateSnapshot("RuntimeMigrator",
					_context.GetType(),
					modelSnapshotName,
					_context.Model);

				byte[] snapshotCode = await MigrationUtils.CompressSource(modelSource);

				// Добавляем в таблицу "modelsnapshot" новый снимок модели БД
				_context.Set<EntityModelSnapshot>()
					.Add(new EntityModelSnapshot
					{
						SnapshotCode = snapshotCode
					});

				await _context.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				_logger.Error(ex, $"Ошибка в процессе сохранения нового снимка модели БД.");

				throw ex;
			}
		}

		private async Task<StepResultBase> UpdateDatabase(IEnumerable<string> migrationsToApply)
		{
			StepResultBase result = null;

			StepResult<string> sqlScriptResult = BuildUpdateSqlScript(migrationsToApply);

			if (sqlScriptResult.Success)
			{
				// Выполняем скрипт обновления БД
				await ExecuteSql(sqlScriptResult.Data);

				// Добавляем новый снимок модели БД в таблицу "modelsnapshot"
				await SaveModelSnapshot(migrationsToApply.Last());

				// Проверяем наличие и при необходимости создаем таблицу истории "__EFMigrationsHistory"
				await EnsureCreateEFHistoryTable();

				// Добавляем в таблицу "__EFMigrationsHistory" примененные миграции
				foreach (string migrationId in migrationsToApply)
					await AddEFHistoryRecord(migrationId);

				string message = migrationsToApply.Count() == 1
					? $"Миграция {migrationsToApply.FirstOrDefault()} успешно применена, БД обновлена."
					: $"Миграции {string.Join(", ", migrationsToApply)} успешно применены, БД обновлена.";

				_logger.Debug(message);
				result = new StepResultBase(message, true);
			}
			else
			{
				result = new StepResultBase(sqlScriptResult.Message, false);
			}

			return result;
		}

		private StepResult<string> BuildUpdateSqlScript(IEnumerable<string> migrationIds)
		{
			StepResult<string> result = new StepResult<string>(true);
			StringBuilder updateSqlScript = new StringBuilder();

			foreach (var migrationId in migrationIds)
			{
				// Получаем миграцию по идентификатору
				StepResult<CustomMigration> migrationResult = GetDeclaredMigration(migrationId);

				if (migrationResult.Success)
				{
					IEnumerable<string> sqlCommands = migrationResult.Data.UpSqlCommands;

					if (sqlCommands != null && sqlCommands.Any())
					{
						// Получаем команды блока "up" и добавляем их в общий скрипт
						string upOperationsScript = MigrationUtils.SqlCommandsToScript(migrationResult.Data.UpSqlCommands);
						updateSqlScript.AppendLine(upOperationsScript);
					}
				}
				else
				{
					_logger.Debug($"Произошла ошибка в процессе формирования sql-скрипта для обновления БД. {migrationResult.Message}.");
					result = new StepResult<string>($"Произошла ошибка в процессе формирования sql-скрипта для обновления БД. {migrationResult.Message}.", false);

					// В случае возникновения ошибки, выходим из цикла
					break;
				}
			}

			if (result.Success)
			{
				result.Data = updateSqlScript.ToString();
			}

			return result;
		}

		/// <summary>
		/// Выполнить sql команду
		/// </summary>
		/// <param name="sqlQuery">Sql команда</param>
		private async Task ExecuteSql(string sqlQuery)
		{
			if (!string.IsNullOrWhiteSpace(sqlQuery))
			{
				using (IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync())
				{
					try
					{
						await _context.Database.ExecuteSqlRawAsync(sqlQuery);
						await transaction.CommitAsync();
					}
					catch (Exception ex)
					{
						transaction.Rollback();
						_logger.Error(ex, $"Ошибка в процессе выполнения sql выражения {sqlQuery}.");

						throw ex;
					}
					finally
					{
						transaction.Dispose();
					}
				}
			}
		}

		/// <summary>
		/// Сгенерировать идентификатор - наименование миграции
		/// </summary>
		private string GenerateMigrationId(bool isInitMigration = false)
		{
			string migrationName = isInitMigration
				? "Init"
				: "Migration";

			return _migrationsScaffolderDependencies
				.MigrationsIdGenerator
				.GenerateId(migrationName);
		}

		/// <summary>
		/// Создание новой миграции на основе измений модели данных
		/// </summary>
		/// <param name="migrationId">Идентификатор миграции</param>
		/// <returns></returns>
		private async Task<CustomMigration> GenerateMigration(string migrationId)
		{
			IMigrationsAssembly migrationAssembly = _context.GetService<IMigrationsAssembly>();

			// Получаем актуальный снимок модели БД
			string modelSnapshotCode = await GetCurrentModelSnapshotCode();

			CustomMigration migration = null;
			ModelSnapshot modelSnapshot = null;

			// Загружаем снимок модели БД в контекст приложения
			if (!string.IsNullOrWhiteSpace(modelSnapshotCode))
			{
				try
				{
					modelSnapshot = MigrationUtils.CompileSnapshot(migrationAssembly.Assembly, _context, modelSnapshotCode);
				}
				catch (Exception ex)
				{
					throw new InvalidOperationException("Произошла ошибка при компиляции кода снимка модели БД.", ex);
				}
			}

			IModel currentModel = modelSnapshot?.Model;
			currentModel = _migrationsScaffolderDependencies
				.SnapshotModelProcessor
				.Process(modelSnapshot?.Model);

			IModel newModel = _migrationsScaffolderDependencies.Model;

			// Формируем набор команд для обновления версии БД
			List<MigrationOperation> upOperations = _migrationsScaffolderDependencies.MigrationsModelDiffer
				.GetDifferences(currentModel?.GetRelationalModel(), newModel?.GetRelationalModel())
				.Where(x => !(x is UpdateDataOperation) && !(x is AddForeignKeyOperation) && !(x is DropForeignKeyOperation))
				.ToList();

			// Формируем набор команд для отката версии БД
			List<MigrationOperation> downOperations = _migrationsScaffolderDependencies.MigrationsModelDiffer
				.GetDifferences(newModel?.GetRelationalModel(), currentModel?.GetRelationalModel())
				.Where(x => !(x is UpdateDataOperation) && !(x is AddForeignKeyOperation) && !(x is DropForeignKeyOperation))
				.ToList();

			// Если удалось сформировать списки команд, то создаем объектное представление новой миграции
			if (upOperations.Any() && downOperations.Any())
			{
				IMigrationsSqlGenerator sqlGenerator = _context.GetService<IMigrationsSqlGenerator>();

				IEnumerable<MigrationCommand> migrationUpSqlCommands = sqlGenerator
					.Generate(upOperations, _context.Model);

				IEnumerable<MigrationCommand> migrationDownSqlCommands = sqlGenerator
					.Generate(downOperations, _context.Model);

				migration = new CustomMigration
				{
					UpSqlCommands = migrationUpSqlCommands
						.Select(x => x.CommandText),
					DownSqlCommands = migrationDownSqlCommands
						.Select(x => x.CommandText),
				};
			}

			return migration;
		}

		private async Task<StepResult<List<string>>> GetMigrationsToApply(IEnumerable<string> migrationIds = null)
		{
			StepResult<List<string>> result = null;

			if (migrationIds != null && migrationIds.Any())
			{
				result = new StepResult<List<string>>(true, migrationIds.ToList());
			}
			else
			{
				result = new StepResult<List<string>>(true, await GetNotAppliedMigrationIds());
			}

			return result;
		}

		#endregion
	}
}
