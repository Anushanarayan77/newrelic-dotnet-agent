﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using NewRelic.Agent.Configuration;
using NewRelic.Agent.Core.Transactions;
using NewRelic.Agent.Helpers;

namespace NewRelic.Agent.Core.Errors
{
	public struct ErrorData
	{
		public readonly string ErrorMessage;

		public readonly string ErrorTypeName;

		public readonly string StackTrace;

		public readonly DateTime NoticedAt;

		public readonly bool IsAnError;

		// used for Custom Error outside a transaction to set value for:
		// 1. ErrorTraceWireModel.Path
		// 2. ErrorEvent transactionName attribute
		public string Path;

		internal static readonly string _stripExceptionMessagesMessage = "Message removed by New Relic based on your currently enabled security settings.";

		private ErrorData(string errorMessage, string errorTypeName, string stackTrace, DateTime noticedAt)
		{
			NoticedAt = noticedAt;
			StackTrace = stackTrace;
			ErrorMessage = errorMessage;
			ErrorTypeName = GetFriendlyExceptionTypeName(errorTypeName);
			IsAnError = true;
			Path = null;
		}

		public static ErrorData FromParts(string errorMessage, string errorTypeName, DateTime noticedAt, bool stripErrorMessage)
		{
			var message = stripErrorMessage ? _stripExceptionMessagesMessage : errorMessage;
			return new ErrorData(message, errorTypeName, null, noticedAt);
		}

		public static ErrorData FromException(Exception exception, bool stripErrorMessage)
		{
			var message = stripErrorMessage ? _stripExceptionMessagesMessage : exception.GetBaseException().Message;
			var exceptionTypeName = exception.GetType().FullName;
			var stackTrace = ExceptionFormatter.FormatStackTrace(exception, stripErrorMessage);
			var noticedAt = DateTime.UtcNow;
			return new ErrorData(message, exceptionTypeName, stackTrace, noticedAt);
		}

		public static ErrorData TryGetErrorData(ImmutableTransaction immutableTransaction, IConfigurationService configurationService)
		{
			// *Any* ignored custom error noticed by the agent should result in no error trace
			var customErrors = immutableTransaction.TransactionMetadata.CustomErrorDatas.ToList();
			if (ShouldIgnoreAnyError(customErrors, configurationService))
				return new ErrorData();

			// Custom errors are more valuable than exception errors or status code errors
			if (customErrors.Any())
			{
				return customErrors.First();
			}

			// An ignored status code should result in no error trace
			var formattedStatusCode = TryGetFormattedStatusCode(immutableTransaction);
			if (ShouldIgnoreError(formattedStatusCode, configurationService))
				return new ErrorData();

			// *Any* ignored exception noticed by the agent should result in no error trace (unless there is a custom error)
			var transactionExceptions = immutableTransaction.TransactionMetadata.TransactionExceptionDatas.ToList();
			if (ShouldIgnoreAnyError(transactionExceptions, configurationService))
				return new ErrorData();

			// Exception errors are more valuable than status code errors
			if (transactionExceptions.Any())
			{
				return transactionExceptions.First();
			}

			return TryCreateHttpErrorData(immutableTransaction);
		}

		private static bool ShouldIgnoreAnyError(IEnumerable<ErrorData> errorData, IConfigurationService configurationService)
		{
			var errorTypeNames = errorData.Select(data => data.ErrorTypeName);
			return ShouldIgnoreAnyError(errorTypeNames, configurationService);
		}

		internal static bool ShouldIgnoreError(string errorTypeName, IConfigurationService configurationService)
		{
			return ShouldIgnoreAnyError(new[] { errorTypeName }, configurationService);
		}

		private static bool ShouldIgnoreAnyError(IEnumerable<string> errorTypeNames, IConfigurationService configurationService)
		{
			foreach (var errorClassName in errorTypeNames)
			{
				if (errorClassName == null)
					continue;

				if (configurationService.Configuration.ExceptionsToIgnore.Contains(errorClassName))
					return true;

				if (configurationService.Configuration.HttpStatusCodesToIgnore.Contains(errorClassName))
					return true;

				var splitStatusCode = errorClassName.Split(StringSeparators.Period);
				if (splitStatusCode[0] != null && configurationService.Configuration.HttpStatusCodesToIgnore.Contains(splitStatusCode[0]))
				{
					return true;
				}
			}

			return false;
		}

		private static string TryGetFormattedStatusCode(ImmutableTransaction immutableTransaction)
		{
			if (immutableTransaction.TransactionMetadata.HttpResponseStatusCode == null)
				return null;

			var statusCode = immutableTransaction.TransactionMetadata.HttpResponseStatusCode.Value;
			var subStatusCode = immutableTransaction.TransactionMetadata.HttpResponseSubStatusCode;

			return subStatusCode == null
				? $"{statusCode}"
				: $"{statusCode}.{subStatusCode}";
		}

		private static ErrorData TryCreateHttpErrorData(ImmutableTransaction immutableTransaction)
		{
			if (immutableTransaction.TransactionMetadata.HttpResponseStatusCode == null)
				return new ErrorData();

			// Only status codes 400+ are considered error status codes
			var statusCode = immutableTransaction.TransactionMetadata.HttpResponseStatusCode.Value;
			if (statusCode < 400)
				return new ErrorData();

			var statusDescription =
#if NETSTANDARD2_0
				statusCode.ToString();
#else
				HttpWorkerRequest.GetStatusDescription(statusCode);
#endif
			var formattedFullStatusCode = TryGetFormattedStatusCode(immutableTransaction);

			var noticedAt = immutableTransaction.StartTime + immutableTransaction.ResponseTimeOrDuration;
			var errorMessage = statusDescription ?? $"Http Error {formattedFullStatusCode}";
			var errorTypeName = formattedFullStatusCode;

			return new ErrorData(errorMessage, errorTypeName, null, noticedAt);
		}

		private static string GetFriendlyExceptionTypeName(string exceptionTypeName)
		{
			return exceptionTypeName?.Split(StringSeparators.BackTick, 2)[0] ?? string.Empty;
		}
	}
}
