﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Prise.Infrastructure;
using Prise.Plugin;

namespace Prise
{
    public class DefaultRemotePluginActivator<T> : IRemotePluginActivator<T>
    {
        private readonly ISharedServicesProvider<T> sharedServicesProvider;
        private ConcurrentBag<object> instances;
        private IServiceCollection services;
        private bool disposed = false;

        public DefaultRemotePluginActivator(ISharedServicesProvider<T> sharedServicesProvider)
        {
            this.sharedServicesProvider = sharedServicesProvider;
            this.instances = new ConcurrentBag<object>();
            this.services = new ServiceCollection();
        }

        private object AddToDisposables(object obj)
        {
            this.instances.Add(obj);
            return obj;
        }

        public virtual object CreateRemoteBootstrapper(Type bootstrapperType, Assembly assembly)
        {
            var contructors = bootstrapperType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            var firstCtor = contructors.First();

            if (!contructors.Any())
                throw new PrisePluginException($"No public constructors found for remote bootstrapper {bootstrapperType.Name}");
            if (firstCtor.GetParameters().Any())
                throw new PrisePluginException($"Bootstrapper {bootstrapperType.Name} must contain a public parameterless constructor");

            return AddToDisposables(assembly.CreateInstance(bootstrapperType.FullName));
        }

        public virtual object CreateRemoteInstance(PluginActivationContext pluginActivationContext, IPluginBootstrapper bootstrapper = null)
        {
            var pluginType = pluginActivationContext.PluginType;
            var pluginAssembly = pluginActivationContext.PluginAssembly;
            var factoryMethod = pluginActivationContext.PluginFactoryMethod;

            var contructors = pluginType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

            if (contructors.Count() > 1)
                throw new PrisePluginException($"Multiple public constructors found for remote plugin {pluginType.Name}");

            var serviceProvider = AddToDisposables(GetServiceProvider(bootstrapper)) as IServiceProvider;

            if (factoryMethod != null)
                return AddToDisposables(factoryMethod.Invoke(null, new[] { serviceProvider }));

            var firstCtor = contructors.FirstOrDefault();
            if (firstCtor != null && !firstCtor.GetParameters().Any()) // Empty default CTOR
            {
                var pluginServiceProvider = AddToDisposables(serviceProvider.GetService<IPluginServiceProvider>()) as IPluginServiceProvider;
                var remoteInstance = pluginAssembly.CreateInstance(pluginType.FullName);
                remoteInstance = InjectFieldsWithServices(remoteInstance, pluginServiceProvider, pluginActivationContext.PluginServices);

                ActivateIfNecessary(remoteInstance, pluginActivationContext);

                return AddToDisposables(remoteInstance);
            }

            throw new PrisePluginException($"Plugin of type {typeof(T).Name} could not be activated.");
        }

        protected virtual void ActivateIfNecessary(object remoteInstance, PluginActivationContext pluginActivationContext)
        {
            if (pluginActivationContext.PluginActivatedMethod == null)
                return;

            var remoteActivationMethod = remoteInstance.GetType().GetRuntimeMethods().FirstOrDefault(m => m.Name == pluginActivationContext.PluginActivatedMethod.Name);
            if (remoteActivationMethod == null)
                throw new PrisePluginException($"Remote activation method {pluginActivationContext.PluginActivatedMethod.Name} not found for plugin {typeof(T).Name} on remote object {remoteInstance.GetType().Name}");

            remoteActivationMethod.Invoke(remoteInstance, null);
        }

        protected virtual object InjectFieldsWithServices(object remoteInstance, IPluginServiceProvider pluginServiceProvider, IEnumerable<PluginService> pluginServices)
        {
            foreach (var pluginService in pluginServices)
            {
                object serviceInstance = null;
                switch (pluginService.ProvidedBy)
                {
                    case ProvidedBy.Host:
                        serviceInstance = pluginServiceProvider.GetHostService(pluginService.ServiceType);
                        break;
                    case ProvidedBy.Plugin:
                        serviceInstance = pluginServiceProvider.GetPluginService(pluginService.ServiceType);
                        break;
                }

                try
                {
                    remoteInstance
                        .GetType()
                        .GetTypeInfo()
                            .DeclaredFields
                                .First(f => f.Name == pluginService.FieldName)
                                .SetValue(remoteInstance, serviceInstance);
                    continue;
                }
                catch (ArgumentException) { }

                if (pluginService.BridgeType == null)
                    throw new PrisePluginException($"Field {pluginService.FieldName} could not be set, please consider using a PluginBridge.");

                var bridgeConstructor = pluginService.BridgeType
                        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(c => c.GetParameters().Count() == 1 && c.GetParameters().First().ParameterType == typeof(object));

                if (bridgeConstructor == null)
                    throw new PrisePluginException($"PluginBridge {pluginService.BridgeType.Name} must have a single public constructor with one parameter of type object.");

                var bridgeInstance = AddToDisposables(bridgeConstructor.Invoke(new[] { serviceInstance }));
                remoteInstance.GetType().GetTypeInfo().DeclaredFields.First(f => f.Name == pluginService.FieldName).SetValue(remoteInstance, bridgeInstance);
            }

            return remoteInstance;
        }

        protected virtual IServiceProvider GetServiceProvider(IPluginBootstrapper bootstrapper)
        {
            var hostServices = this.sharedServicesProvider.ProvideHostServices();
            var sharedServices = this.sharedServicesProvider.ProvideSharedServices();

            foreach (var service in hostServices)
                this.services.Add(service);

            foreach (var service in sharedServices)
                this.services.Add(service);

            if (bootstrapper != null)
                sharedServices = bootstrapper.Bootstrap(this.services);

            this.services.AddScoped<IPluginServiceProvider>(sp => new DefaultPluginServiceProvider(
                sp,
                hostServices.Select(d => d.ServiceType),
                sharedServices.Select(d => d.ServiceType)
            ));

            return this.services.BuildServiceProvider();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed && disposing)
            {
                foreach (var disposable in this.instances)
                {
                    if (disposable as IDisposable != null)
                        (disposable as IDisposable)?.Dispose();
                }
                instances.Clear();

                this.instances = null;
                this.services = null;
            }
            this.disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}