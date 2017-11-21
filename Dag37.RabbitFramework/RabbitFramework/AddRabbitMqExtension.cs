﻿using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace RabbitFramework
{
    public static class AddRabbitMqExtension
    {
        public static IServiceCollection AddRabbitMq(this IServiceCollection serviceCollection, BusOptions options)
        {
            return serviceCollection.AddTransient<IBusProvider>((ctx) =>
            {
                return new RabbitBusProvider(options);
            });
        }

        public static void UseRabbitMq(this IServiceProvider serviceProvider)
        {
            var busProvider = serviceProvider.GetService<IBusProvider>();

            if(busProvider == null)
            {
                throw new NotImplementedException("You did not use the AddRabbitMq method on the service collection");
            }

            var initializer = new RabbitInitializer(serviceProvider.GetService<IBusProvider>(), serviceProvider);

            initializer.Initialize(Assembly.GetCallingAssembly());
        }
    }
}