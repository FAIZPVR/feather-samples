﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Hosting;
using System.Web.Mvc;

using Telerik.Sitefinity.Frontend.Mvc.Controllers;
using Telerik.Sitefinity.Frontend.Mvc.Infrastructure.Controllers;
using Telerik.Sitefinity.Frontend.Mvc.Infrastructure.Controllers.Attributes;

namespace PrecompiledViewsCrawler
{
    public class ViewEngineFilterAttribute : ActionFilterAttribute
    {
        public ViewEngineFilterAttribute()
        {
        }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            Controller controller = filterContext.Controller as Controller;
            if (controller == null)
            {
                return;
            }

            if (filterContext.HttpContext.Request.Headers.GetValues("X-Crawler") != null)
            {
                this.RegisterPrecompiledViewEngines(this.ControllerContainerAssemblies, (Controller)filterContext.Controller, typeof(ExtendedCompositePrecompiledMvcEngineWrapper));
            }
            else
            {
                this.RemoveExtendedPrecompiledMvcEngineWrapper(((Controller)filterContext.Controller).ViewEngineCollection);
            }

            FrontendControllerFactory.EnhanceViewEngines(controller);
            base.OnActionExecuting(filterContext);
        }

        public IEnumerable<Assembly> ControllerContainerAssemblies
        {
            get
            {
                if (ViewEngineFilterAttribute.controllerContainerAssemblies == null)
                {
                    lock (ViewEngineFilterAttribute.ControllerContainerAssembliesLock)
                    {
                        if (ViewEngineFilterAttribute.controllerContainerAssemblies == null)
                        {
                            ViewEngineFilterAttribute.controllerContainerAssemblies = this.RetrieveAssemblies();
                        }
                    }
                }

                return ViewEngineFilterAttribute.controllerContainerAssemblies;
            }

            private set
            {
                lock (ViewEngineFilterAttribute.ControllerContainerAssembliesLock)
                {
                    ViewEngineFilterAttribute.controllerContainerAssemblies = value;
                }
            }
        }

        public IEnumerable<Assembly> RetrieveAssemblies()
        {
            var assemblyFileNames = this.RetrieveAssembliesFileNames().ToArray();
            var result = new List<Assembly>();

            foreach (var assemblyFileName in assemblyFileNames)
            {
                if (this.IsControllerContainer(assemblyFileName))
                {
                    var assembly = this.LoadAssembly(assemblyFileName);
                    this.InitializeControllerContainer(assembly);

                    result.Add(assembly);
                }

                if (this.IsMarkedAssembly<ResourcePackageAttribute>(assemblyFileName))
                {
                    result.Add(this.LoadAssembly(assemblyFileName));
                }
            }

            return result;
        }

        protected virtual IEnumerable<string> RetrieveAssembliesFileNames()
        {
            var controllerAssemblyPath = Path.Combine(HostingEnvironment.ApplicationPhysicalPath, "bin");
            return Directory.EnumerateFiles(controllerAssemblyPath, "*.dll", SearchOption.TopDirectoryOnly);
        }

        protected virtual bool IsControllerContainer(string assemblyFileName)
        {
            return this.IsMarkedAssembly<ControllerContainerAttribute>(assemblyFileName);
        }

        protected virtual Assembly LoadAssembly(string assemblyFileName)
        {
            return Assembly.LoadFrom(assemblyFileName);
        }

        protected virtual void InitializeControllerContainer(Assembly container)
        {
            if (container == null)
                throw new ArgumentNullException("container");

            var containerAttribute = container.GetCustomAttributes(false).Single(attr => attr.GetType().AssemblyQualifiedName == typeof(ControllerContainerAttribute).AssemblyQualifiedName) as ControllerContainerAttribute;

            if (containerAttribute.InitializationType == null || containerAttribute.InitializationMethod.IsNullOrWhitespace())
                return;

            var initializationMethod = containerAttribute.InitializationType.GetMethod(containerAttribute.InitializationMethod);
            initializationMethod.Invoke(null, null);
        }

        private bool IsMarkedAssembly<TAttribute>(string assemblyFileName)
            where TAttribute : Attribute
        {
            if (assemblyFileName == null)
                return false;

            bool result;
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += this.CurrentDomain_ReflectionOnlyAssemblyResolve;
            try
            {
                try
                {
                    var reflOnlyAssembly = Assembly.ReflectionOnlyLoadFrom(assemblyFileName);

                    result = reflOnlyAssembly != null &&
                            reflOnlyAssembly.GetCustomAttributesData()
                                .Any(d => d.Constructor.DeclaringType.AssemblyQualifiedName == typeof(TAttribute).AssemblyQualifiedName);
                }
                catch (IOException)
                {
                    // We might not be able to load some .DLL files as .NET assemblies. Those files cannot contain controllers.
                    result = false;
                }
                catch (BadImageFormatException)
                {
                    result = false;
                }
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= this.CurrentDomain_ReflectionOnlyAssemblyResolve;
            }

            return result;
        }

        private Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assWithPolicy = AppDomain.CurrentDomain.ApplyPolicy(args.Name);

            return Assembly.ReflectionOnlyLoad(assWithPolicy);
        }

        private void RegisterPrecompiledViewEngines(IEnumerable<Assembly> assemblies, Controller controller, Type viewEngineType)
        {
            var precompiledAssemblies = assemblies.Where(a => a.GetCustomAttribute<ControllerContainerAttribute>() != null).Select(a => new PrecompiledViewAssemblyWrapper(a, null)).ToArray();
            if (precompiledAssemblies.Length > 0)
            {
                controller.ViewEngineCollection.Insert(0, Activator.CreateInstance(viewEngineType, precompiledAssemblies) as IViewEngine);
            }

            var precompiledResourcePackages = this.PrecompiledResourcePackages(assemblies);
            foreach (var package in precompiledResourcePackages.Keys)
            {
                if (package == null)
                    continue;

                var packageAssemblies = precompiledResourcePackages[package];
                if (packageAssemblies.Count > 0)
                {
                    controller.ViewEngineCollection.Insert(0, Activator.CreateInstance(viewEngineType, packageAssemblies, null, package) as IViewEngine);
                }
            }
        }

        private string AssemblyPackage(Assembly assembly)
        {
            var attribute = assembly.GetCustomAttribute<ResourcePackageAttribute>();
            if (attribute == null)
                return null;
            else
                return attribute.Name;
        }

        private Dictionary<string, List<PrecompiledViewAssemblyWrapper>> PrecompiledResourcePackages(IEnumerable<Assembly> assemblies)
        {
            var precompiledViewAssemblies = new Dictionary<string, List<PrecompiledViewAssemblyWrapper>>();
            foreach (var assembly in assemblies)
            {
                var package = this.AssemblyPackage(assembly);
                if (package == null)
                    continue;

                if (!precompiledViewAssemblies.ContainsKey(package))
                    precompiledViewAssemblies[package] = new List<PrecompiledViewAssemblyWrapper>();

                precompiledViewAssemblies[package].Add(new PrecompiledViewAssemblyWrapper(assembly, package));
            }

            return precompiledViewAssemblies;
        }

        private void RemoveExtendedPrecompiledMvcEngineWrapper(ViewEngineCollection viewEngines)
        {
            var engine = viewEngines.FirstOrDefault(ve => ve.GetType() == typeof(ExtendedCompositePrecompiledMvcEngineWrapper));
            while (engine != null)
            {
                viewEngines.Remove(engine);
                engine = viewEngines.FirstOrDefault(ve => ve.GetType() == typeof(ExtendedCompositePrecompiledMvcEngineWrapper));
            }
        }

        private static IEnumerable<Assembly> controllerContainerAssemblies;
        private static readonly object ControllerContainerAssembliesLock = new object();
    }
}
