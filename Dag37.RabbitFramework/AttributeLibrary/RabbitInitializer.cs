﻿using AttributeLibrary.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using RabbitFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AttributeLibrary
{
    public class RabbitInitializer
    {
        private readonly IBusProvider _busProvider;
        private readonly IServiceProvider _serviceProvider;

        public RabbitInitializer(IBusProvider busProvider, IServiceProvider serviceProvider)
        {
            _busProvider = busProvider;
            _serviceProvider = serviceProvider;
        }

        public void Initialize(Assembly executingAssembly)
        {
            _busProvider.CreateConnection();

            var types = executingAssembly.GetTypes();
            InitializeEventListeners(types);
        }

        private void InitializeEventListeners(Type[] types)
        {
            foreach (var type in types)
            {
                var queueAttribute = type.GetCustomAttribute<QueueListenerAttribute>();

                if (queueAttribute != null)
                {
                    if(TypeContainsBothCommandsAndEvents(type))
                    {
                        throw new InvalidOperationException("Type {} can't contain both events and commands. Events and commands should not be sent to the same queue");
                    }

                    SetUpTopicMethods(type, queueAttribute.QueueName);
                    SetUpCommandMethods(type, queueAttribute.QueueName);
                }
            }
        }

        private bool TypeContainsBothCommandsAndEvents(Type type)
        {
            var methods = type.GetMethods();

            return
                methods.Any(m => m.GetCustomAttributes<TopicAttribute>() != null) &&
                methods.Any(m => m.GetCustomAttribute<CommandAttribute>() != null);
        }

        private void SetUpCommandMethods(Type type, string queueName)
        {
            Dictionary<string, MethodInfo> commandsWithMethods = GetCommandsWithMethods(type);
            // Setup RPC listener met dingen
        }

        private void SetUpTopicMethods(Type type, string queueName)
        {
            Dictionary<string, MethodInfo> topicsWithMethods = GetTopicsWithMethods(type);
            _busProvider.CreateQueueWithTopics(queueName, topicsWithMethods.Keys);
            var callback = CreateEventReceivedCallback(type, topicsWithMethods);
            _busProvider.BasicConsume(queueName, callback);
        }

        private Dictionary<string, MethodInfo> GetCommandsWithMethods(Type type)
        {
            return GetAttributeValuesWithMethod<CommandAttribute>(type, (a) => a.CommandType);
        }

        public Dictionary<string, MethodInfo> GetTopicsWithMethods(Type type)
        {
            return GetAttributeValuesWithMethod<TopicAttribute>(type, (a) => a.Topic);
        }


        private Dictionary<string, MethodInfo> GetAttributeValuesWithMethod<TAttribute>(Type type, Func<TAttribute, string> predicate) where TAttribute : Attribute
        {
            return type.GetMethods()
                .Select(methodInfo => new { methodInfo, attribute = methodInfo.GetCustomAttribute<TAttribute>() })
                .Where(methodInfo => methodInfo != null)
                .ToDictionary(m => predicate(m.attribute), m => m.methodInfo);
        }

        public EventReceivedCallback CreateEventReceivedCallback(Type type, Dictionary<string, MethodInfo> topics)
        {
            return (message) =>
            {
                var instance = ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, type);

                var topicMatches = GetTopicMatches(message.RoutingKey, topics);

                foreach (var topic in topicMatches)
                {
                    InvokeTopic(message, instance, topic);
                }
            };
        }

        public Dictionary<string, MethodInfo> GetTopicMatches(string routingKey, Dictionary<string, MethodInfo> topics)
        {
            var regexHashTag = @"\w+(\.\w+)*";
            var regexStar = @"[\w]+";
            var topicMatches = new Dictionary<string, MethodInfo>();

            foreach (var topic in topics)
            {
                var pattern = topic.Key
                    .Replace(".", "\\.")
                    .Replace("*", regexStar)
                    .Replace("#", regexHashTag);

                pattern = $"^{pattern}$";

                if (Regex.IsMatch(routingKey, pattern))
                {
                    topicMatches.Add(topic.Key, topic.Value);
                }
            }

            return topicMatches;
        }

        private void InvokeTopic(EventMessage message, object instance, KeyValuePair<string, MethodInfo> topic)
        {
            try
            {
                var parameters = topic.Value.GetParameters();
                var parameter = parameters.FirstOrDefault();
                var paramType = parameter.ParameterType;
                var arguments = JsonConvert.DeserializeObject(message.JsonMessage, paramType);

                topic.Value.Invoke(instance, new object[] { arguments });
            }
            catch (TargetInvocationException)
            {
                throw;
            }
        }
    }
}