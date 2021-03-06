﻿using System;
using System.Reflection;
using System.Threading.Tasks;
using EnsureThat;
using Newtonsoft.Json;
using Remiworks.Core.Command.Listener.Callbacks;
using Remiworks.Core.Exceptions;
using Remiworks.Core.Models;

namespace Remiworks.Core.Command.Listener
{
    public class CommandListener : ICommandListener
    {
        private const string KeyContainsWildcardMessage = "Key should not contain wildcards";
        private const string StarWildcard = "*";
        private const string HashtagWildcard = "#";

        private readonly IBusProvider _busProvider;
        private readonly ICommandCallbackRegistry _callbackRegistry;

        public CommandListener(IBusProvider busProvider, ICommandCallbackRegistry commandCallbackRegistry)
        {
            _busProvider = busProvider;
            _callbackRegistry = commandCallbackRegistry;

            _busProvider.EnsureConnection();
        }

        public Task SetupCommandListenerAsync<TParam>(string queueName, string key, CommandReceivedCallback<TParam> callback, string exchangeName = null)
        {
            EnsureArg.IsNotNullOrWhiteSpace(queueName, nameof(queueName));
            EnsureArg.IsNotNullOrWhiteSpace(key, nameof(key));
            EnsureArg.IsNotNull(callback, nameof(callback));
            if (!IsValidKey(key)) throw new ArgumentException(KeyContainsWildcardMessage, nameof(key));

            return SetupCommandListenerAsync(
                queueName,
                key,
                parameter => callback((TParam)parameter),
                typeof(TParam),
                exchangeName);
        }

        public Task SetupCommandListenerAsync(string queueName, string key, CommandReceivedCallback callback, Type parameterType, string exchangeName = null)
        {
            EnsureArg.IsNotNullOrWhiteSpace(queueName, nameof(queueName));
            EnsureArg.IsNotNullOrWhiteSpace(key, nameof(key));
            EnsureArg.IsNotNull(callback, nameof(callback));
            EnsureArg.IsNotNull(parameterType, nameof(parameterType));
            if (!IsValidKey(key)) throw new ArgumentException(KeyContainsWildcardMessage, nameof(key));

            return Task.Run(() =>
            {
                _callbackRegistry.AddCallbackForQueue(
                    queueName,
                    key,
                    message => HandleReceivedCommand(callback, message, parameterType, exchangeName));
            });
        }

        private void HandleReceivedCommand(CommandReceivedCallback callback, EventMessage receivedEventMessage, Type parameterType, string exchangeName)
        {
            Task.Run(async () =>
            {
                object response = null;
                var isError = false;

                var replyKey = $"{receivedEventMessage.RoutingKey}.Reply";
                _busProvider.BasicTopicBind(receivedEventMessage.ReplyQueueName, replyKey);

                try
                {
                    var deserializedParameter = JsonConvert.DeserializeObject(receivedEventMessage.JsonMessage, parameterType);
                    response = await callback(deserializedParameter);
                }
                catch (TargetInvocationException ex)
                {
                    response = new CommandPublisherException(ex.InnerException.Message, ex.InnerException);
                    isError = true;
                }
                catch (Exception ex)
                {
                    response = new CommandPublisherException(ex.Message, ex);
                    isError = true;
                }
                finally
                {
                    if (exchangeName == null)
                    {
                        _busProvider.BasicPublish(new EventMessage
                        {
                            CorrelationId = receivedEventMessage.CorrelationId,
                            IsError = isError,
                            JsonMessage = JsonConvert.SerializeObject(response),
                            RoutingKey = replyKey
                        });
                    }
                    else
                    {
                        _busProvider.BasicPublish(new EventMessage
                        {
                            CorrelationId = receivedEventMessage.CorrelationId,
                            IsError = isError,
                            JsonMessage = JsonConvert.SerializeObject(response),
                            RoutingKey = replyKey
                        },
                        exchangeName);
                    }

                    _busProvider.BasicAcknowledge(receivedEventMessage.DeliveryTag, false);
                }
            });
        }

        private static bool IsValidKey(string key)
        {
            return
                !key.Contains(StarWildcard) &&
                !key.Contains(HashtagWildcard);
        }
    }
}