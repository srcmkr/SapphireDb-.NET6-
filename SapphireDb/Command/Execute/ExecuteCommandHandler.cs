﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SapphireDb.Actions;
using SapphireDb.Connection;
using SapphireDb.Helper;
using SapphireDb.Internal;
using SapphireDb.Models;
using SapphireDb.Models.Exceptions;

namespace SapphireDb.Command.Execute
{
    class ExecuteCommandHandler : CommandHandlerBase, ICommandHandler<ExecuteCommand>, INeedsConnection
    {
        private readonly ActionMapper _actionMapper;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ExecuteCommandHandler> _logger;
        public SignalRConnection Connection { get; set; }

        public ExecuteCommandHandler(DbContextAccesor contextAccessor, ActionMapper actionMapper,
            IServiceProvider serviceProvider, ILogger<ExecuteCommandHandler> logger)
            : base(contextAccessor)
        {
            _actionMapper = actionMapper;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<ResponseBase> Handle(IConnectionInformation context, ExecuteCommand command,
            ExecutionContext executionContext)
        {
            try
            {
                return await GetActionDetails(command, context, executionContext);
            }
            catch (RuntimeBinderException)
            {
                return new ExecuteResponse()
                {
                    ReferenceId = command.ReferenceId,
                    Result = null
                };
            }
        }

        private async Task<ResponseBase> GetActionDetails(ExecuteCommand command, IConnectionInformation context, ExecutionContext executionContext)
        {
            string[] actionParts = command?.Action.Split('.');

            if (actionParts == null || actionParts.Length != 2)
            {
                throw new WrongActionFormatException(command?.Action);
            }

            string actionHandlerName = actionParts[0];
            string actionName = actionParts[1];

            Type actionHandlerType = _actionMapper.GetHandler(actionHandlerName);

            if (actionHandlerType == null)
            {
                throw new ActionHandlerNotFoundException(actionHandlerName);
            }
            
            MethodInfo actionMethod = _actionMapper.GetAction(actionName, actionHandlerType);

            if (actionMethod == null)
            {
                throw new ActionNotFoundException(actionHandlerName, actionName);
            }
            
            ActionHandlerBase actionHandler = (ActionHandlerBase) _serviceProvider.GetService(actionHandlerType);

            if (actionHandler == null)
            {
                throw new ActionHandlerNotFoundException(actionHandlerName);
            }
            
            actionHandler.connection = Connection;
            actionHandler.executeCommand = command;

            if (!actionHandlerType.CanExecuteAction(context, actionHandler, _serviceProvider))
            {
                throw new UnauthorizedException("User is not allowed to execute actions of this handler");
            }

            if (!actionMethod.CanExecuteAction(context, actionHandler, _serviceProvider))
            {
                throw new UnauthorizedException("User is not allowed to execute action");
            }

            return await ExecuteAction(actionHandler, command, actionMethod, executionContext);
        }

        private async Task<ResponseBase> ExecuteAction(ActionHandlerBase actionHandler, ExecuteCommand command,
            MethodInfo actionMethod, ExecutionContext executionContext)
        {
            _logger.LogDebug("Execution of {ActionHandlerName}.{ActionName} started. ConnectionId: {SapphireConnectionId}, ExecutionId: {ExecutionId}", actionMethod.DeclaringType?.FullName,
                actionMethod.Name, Connection.Id, executionContext.Id);

            object result = actionMethod.Invoke(actionHandler, GetParameters(actionMethod, command));

            if (result != null)
            {
                if (ActionHelper.HandleAsyncEnumerable(result, actionHandler.AsyncResult))
                {
                    result = null;
                }
                else
                {
                    result = await ActionHelper.HandleAsyncResult(result);
                }
            }

            _logger.LogInformation("Executed {ActionHandlerName}.{ActionName}. ConnectionId: {SapphireConnectionId}, ExecutionId: {ExecutionId}", actionMethod.DeclaringType?.FullName, actionMethod.Name, Connection.Id, executionContext.Id);

            return new ExecuteResponse()
            {
                ReferenceId = command.ReferenceId,
                Result = result
            };
        }

        private object[] GetParameters(MethodInfo actionMethod, ExecuteCommand command)
        {
            return actionMethod.GetParameters().Select(parameter =>
            {
                if (parameter.Position >= command.Parameters.Length)
                {
                    return null;
                }

                if (parameter.ParameterType.IsGenericType &&
                    parameter.ParameterType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                {
                    SapphireStreamHelper streamHelper =
                        (SapphireStreamHelper) _serviceProvider.GetService(typeof(SapphireStreamHelper));
                    return streamHelper.OpenStreamChannel(Connection, command, parameter.ParameterType, _serviceProvider);
                }

                JToken parameterValue = command.Parameters[parameter.Position];
                return parameterValue?.ToObject(parameter.ParameterType);
            }).ToArray();
        }
    }
}