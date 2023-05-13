using System;

namespace RuntimeMigrator.Models
{
	/// <summary>
	/// Класс для передачи данных о выполнении какой-либо операции
	/// </summary>
	public class StepResultBase
	{
		public StepResultBase()
		{
			this.StatusCode = 500;
		}

		public StepResultBase(string message)
		{
			this.StatusCode = 500;
			this.Message = message;
		}

		public StepResultBase(string message, int statusCode)
		{
			this.StatusCode = statusCode;
			this.Message = message;
		}

		public StepResultBase(string message, bool success)
		{
			this.Success = success;
			this.Message = message;
		}

		public StepResultBase(string message, bool success, Exception exception)
		{
			this.Success = success;
			this.Message = message;
			this.Exception = exception;
		}

		public StepResultBase(bool success)
		{
			this.Success = success;
		}

		/// <summary>
		/// Статус-код операции (при необходимости)
		/// </summary>
		public int StatusCode { get; set; }

		/// <summary>
		/// Признак успеха опрерации
		/// </summary>
		public bool Success { get; set; }

		/// <summary>
		/// Сообщение об операции. Это может быть текст ошибки или просто сообщение
		/// </summary>
		public string Message { get; set; }

		/// <summary>
		/// Объект с исключением
		/// </summary>
		public Exception Exception { get; set; }
	}

	/// <summary>
	/// Класс для передачи данных о выполнении какой-либо операции с возвратом каких-либо данных в поле Data
	/// </summary>
	public class StepResult<T> : StepResultBase
	{
		public StepResult()
			: base()
		{
		}

		public StepResult(bool success)
			: base(success)
		{
		}

		public StepResult(string message)
			: base(message)
		{
		}

		public StepResult(string message, int statusCode)
			: base(message, statusCode)
		{
		}

		public StepResult(string message, bool success)
			: base(message, success)
		{
		}

		public StepResult(bool success, T data)
			: base(success)
		{
			this.Data = data;
		}

		public StepResult(string message, bool success, T data)
			: base(message, success)
		{
			this.Data = data;
		}

		/// <summary>
		/// Объект с данными операции
		/// </summary>
		public T Data { get; set; }
	}
}
