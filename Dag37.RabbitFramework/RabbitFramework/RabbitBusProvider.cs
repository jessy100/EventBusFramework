﻿using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RabbitFramework
{
    public class RabbitBusProvider : IBusProvider
    {
        private const string ExchangeType = "topic";

        private readonly ConcurrentDictionary<string, Action<string>> _commandCallbacks;
        private readonly string _replyQueueName;
        private readonly EventingBasicConsumer _consumer;

        private IConnection _connection;
        private IModel _channel;

        public BusOptions BusOptions { get; }

        public RabbitBusProvider(BusOptions busOptions)
        {
            BusOptions = busOptions;

            CreateConnection();

            _replyQueueName = _channel.QueueDeclare().QueueName;
            _commandCallbacks = new ConcurrentDictionary<string, Action<string>>();

            _consumer = new EventingBasicConsumer(_channel);
            _consumer.Received += HandleReceivedCommandCallback;
        }

        private void CreateConnection()
        {
            var factory = new ConnectionFactory()
            {
                HostName = BusOptions.Hostname,
                VirtualHost = BusOptions.VirtualHost
            };

            if (BusOptions.Port != null)
            {
                factory.Port = BusOptions.Port.Value;
            }

            if (BusOptions.UserName != null)
            {
                factory.UserName = BusOptions.UserName;
            }

            if (BusOptions.Password != null)
            {
                factory.Password = BusOptions.Password;
            }

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            _channel.ExchangeDeclare(BusOptions.ExchangeName, ExchangeType);
        }

        public void BasicConsume(string queueName, EventReceivedCallback callback)
        {
            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new ArgumentException(nameof(queueName));
            }

            if (callback == null)
            {
                throw new ArgumentException(nameof(callback));
            }

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += (sender, args) => HandleReceivedEvent(args, callback);

            _channel.BasicConsume(queueName, true, consumer);
        }

        public void CreateQueueWithTopics(string queueName, IEnumerable<string> topics)
        {
            _channel.QueueDeclare(queue: queueName, exclusive: false);

            topics.ToList().ForEach(topic =>
                _channel.QueueBind(queueName, BusOptions.ExchangeName, topic));
        }

        public void BasicPublish(EventMessage message)
        {
            _channel.BasicPublish(exchange: BusOptions.ExchangeName,
                                 routingKey: message.RoutingKey,
                                 basicProperties: null,
                                 body: Encoding.UTF8.GetBytes(message.JsonMessage));
        }

        public void SetupRpcListener<TParam>(string queue, CommandReceivedCallback<TParam> function)
        {
            _channel.QueueDeclare(
                queue: queue,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            _channel.BasicQos(0, 1, false);
            var consumer = new EventingBasicConsumer(_channel);

            consumer.Received += (sender, args) => HandleReceivedCommand(function, args);

            _channel.BasicConsume(
                queue: queue,
                autoAck: false,
                consumer: consumer);
        }

        public async Task<T> Call<T>(string queueName, object message, int timeout = 5000)
        {
            var correlationId = Guid.NewGuid().ToString();

            var waitHandle = new ManualResetEvent(false);
            string responseJson = null;

            _commandCallbacks[correlationId] = (response) =>
            {
                responseJson = response;
                waitHandle.Set();
            };

            SendCommand(queueName, correlationId, message);
            Task<bool> waitForHandle = Task.Run(() => waitHandle.WaitOne(timeout));

            bool gotResponse = await waitForHandle;

            return gotResponse
                ? JsonConvert.DeserializeObject<T>(responseJson)
                : throw new TimeoutException($"Could not get response for the command '{correlationId}' in queue '{queueName}'");
        }

        private void HandleReceivedEvent(BasicDeliverEventArgs args, EventReceivedCallback callback)
        {
            var message = Encoding.UTF8.GetString(args.Body);

            Guid? correlationId = null;

            if (args.BasicProperties.CorrelationId != null &&
               Guid.TryParse(args.BasicProperties.CorrelationId, out Guid parsedId))
            {
                correlationId = parsedId;
            }

            EventMessage eventMessage = new EventMessage()
            {
                JsonMessage = message,
                RoutingKey = args.RoutingKey,
                CorrelationId = correlationId,
                Timestamp = args.BasicProperties.Timestamp.UnixTime,
                ReplyQueueName = args.BasicProperties.ReplyTo,
                Type = args.BasicProperties.Type
            };

            callback(eventMessage);
        }

        private void HandleReceivedCommand<TParam>(CommandReceivedCallback<TParam> function, BasicDeliverEventArgs args)
        {
            var replyProps = _channel.CreateBasicProperties();
            replyProps.CorrelationId = args.BasicProperties.CorrelationId;

            var bodyJson = Encoding.UTF8.GetString(args.Body);
            TParam bodyObject = JsonConvert.DeserializeObject<TParam>(bodyJson);

            object functionResult = function(bodyObject);
            string response = JsonConvert.SerializeObject(functionResult);

            var responseBytes = Encoding.UTF8.GetBytes(response);

            _channel.BasicPublish(
                exchange: "",
                routingKey: args.BasicProperties.ReplyTo,
                basicProperties: replyProps,
                body: responseBytes);

            _channel.BasicAck(
                deliveryTag: args.DeliveryTag,
                multiple: false);
        }

        private void SendCommand(string queueName, string correlationId, object message)
        {
            var messageBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));

            _channel.BasicPublish(
                exchange: "",
                routingKey: queueName,
                basicProperties: CreateProperties(correlationId),
                body: messageBytes);

            _channel.BasicConsume(
                consumer: _consumer,
                queue: _replyQueueName,
                autoAck: true);
        }

        private void HandleReceivedCommandCallback(object model, BasicDeliverEventArgs args)
        {
            var body = args.Body;
            string responsejson = Encoding.UTF8.GetString(body);

            string correlationId = args.BasicProperties.CorrelationId;

            if (_commandCallbacks.ContainsKey(correlationId))
            {
                _commandCallbacks[correlationId](responsejson);
            }
        }

        private IBasicProperties CreateProperties(string correlationId)
        {
            var properties = _channel.CreateBasicProperties();
            properties.CorrelationId = correlationId;
            properties.ReplyTo = _replyQueueName;

            return properties;
        }

        public void Dispose()
        {
            _connection.Dispose();
            _channel.Dispose();
        }
    }
}