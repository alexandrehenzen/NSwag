﻿//-----------------------------------------------------------------------
// <copyright file="OperationParameterProcessor.cs" company="NSwag">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>https://github.com/NSwag/NSwag/blob/master/LICENSE.md</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NJsonSchema;
using NJsonSchema.Generation;
using NJsonSchema.Infrastructure;
using NSwag.SwaggerGeneration.Processors;
using NSwag.SwaggerGeneration.Processors.Contexts;
using NSwag.SwaggerGeneration.WebApi.Infrastructure;

namespace NSwag.SwaggerGeneration.WebApi.Processors
{
    /// <summary>Generates the operation's parameters.</summary>
    public class OperationParameterProcessor : IOperationProcessor
    {
        private readonly WebApiToSwaggerGeneratorSettings _settings;

        /// <summary>Initializes a new instance of the <see cref="OperationParameterProcessor"/> class.</summary>
        /// <param name="settings">The settings.</param>
        public OperationParameterProcessor(WebApiToSwaggerGeneratorSettings settings)
        {
            _settings = settings;
        }

        /// <summary>Processes the specified method information.</summary>
        /// <param name="context"></param>
        /// <returns>true if the operation should be added to the Swagger specification.</returns>
        public async Task<bool> ProcessAsync(OperationProcessorContext context)
        {
            var httpPath = context.OperationDescription.Path;
            var parameters = context.MethodInfo.GetParameters().ToList();
            foreach (var parameter in parameters.Where(p => p.ParameterType != typeof(CancellationToken) &&
                                                            p.GetCustomAttributes().All(a => a.GetType().Name != "SwaggerIgnoreAttribute") &&
                                                            p.GetCustomAttributes().All(a => a.GetType().Name != "FromServicesAttribute") &&
                                                            p.GetCustomAttributes().All(a => a.GetType().Name != "BindNeverAttribute")))
            {
                var parameterName = parameter.Name;
                var parameterAttributes = parameter.GetCustomAttributes().ToList();

                dynamic fromBodyAttribute = parameterAttributes.SingleOrDefault(a => a.GetType().Name == "FromBodyAttribute");
                dynamic fromUriAttribute = parameterAttributes.SingleOrDefault(a => a.GetType().Name == "FromUriAttribute" || a.GetType().Name == "FromQueryAttribute");
                dynamic fromRouteAttribute = parameterAttributes.SingleOrDefault(a => a.GetType().FullName == "Microsoft.AspNetCore.Mvc.FromRouteAttribute");
                dynamic fromHeaderAttribute = parameterAttributes.SingleOrDefault(a => a.GetType().FullName == "Microsoft.AspNetCore.Mvc.FromHeaderAttribute");

                string bodyParameterName = TryGetStringPropertyValue(fromBodyAttribute, "Name") ?? parameterName;
                string uriParameterName = TryGetStringPropertyValue(fromUriAttribute, "Name") ?? parameterName;

                var uriParameterNameLower = uriParameterName.ToLowerInvariant();

                if (httpPath.ToLowerInvariant().Contains("{" + uriParameterNameLower + "}") ||
                    httpPath.ToLowerInvariant().Contains("{" + uriParameterNameLower + ":")) // path parameter
                {
                    var operationParameter = await context.SwaggerGenerator.CreatePrimitiveParameterAsync(uriParameterName, parameter).ConfigureAwait(false);
                    operationParameter.Kind = SwaggerParameterKind.Path;
                    operationParameter.IsNullableRaw = false;
                    operationParameter.IsRequired = true; // Path is always required => property not needed

                    context.OperationDescription.Operation.Parameters.Add(operationParameter);
                }
                else
                {
                    var parameterInfo = JsonObjectTypeDescription.FromType(parameter.ParameterType, _settings.ResolveContract(parameter.ParameterType), parameter.GetCustomAttributes(), _settings.DefaultEnumHandling);
                    if (await TryAddFileParameterAsync(parameterInfo, context.OperationDescription.Operation, parameter, context.SwaggerGenerator).ConfigureAwait(false) == false)
                    {
                        if (fromRouteAttribute != null)
                        {
                            parameterName = !string.IsNullOrEmpty(fromRouteAttribute.Name) ? fromRouteAttribute.Name : parameter.Name;

                            var operationParameter = await context.SwaggerGenerator.CreatePrimitiveParameterAsync(parameterName, parameter).ConfigureAwait(false);
                            operationParameter.Kind = SwaggerParameterKind.Path;
                            operationParameter.IsNullableRaw = false;
                            operationParameter.IsRequired = true;

                            context.OperationDescription.Operation.Parameters.Add(operationParameter);
                        }
                        else if (fromHeaderAttribute != null)
                        {
                            parameterName = !string.IsNullOrEmpty(fromHeaderAttribute.Name) ? fromHeaderAttribute.Name : parameter.Name;

                            var operationParameter = await context.SwaggerGenerator.CreatePrimitiveParameterAsync(parameterName, parameter).ConfigureAwait(false);
                            operationParameter.Kind = SwaggerParameterKind.Header;

                            context.OperationDescription.Operation.Parameters.Add(operationParameter);
                        }
                        else
                        {

                            if (parameterInfo.IsComplexType)
                            {
                                // Check for a custom ParameterBindingAttribute (OWIN/WebAPI only)
                                Attribute bindingAttribute = null;
                                if (fromBodyAttribute == null &&
                                    fromUriAttribute == null &&
                                    !_settings.IsAspNetCore &&
                                    (bindingAttribute = parameterAttributes.SingleOrDefault(attr => attr.GetType().InheritsFrom("ParameterBindingAttribute", TypeNameStyle.Name))) != null)
                                {
                                    // Try to find a [WillReadBody] attribute on either the action parameter or the bindingAttribute's class
                                    Attribute willReadBodyAttribute = parameterAttributes.Concat(bindingAttribute.GetType().GetTypeInfo().GetCustomAttributes())
                                        .FirstOrDefault(attr => attr.GetType().Name.Equals("WillReadBodyAttribute", StringComparison.OrdinalIgnoreCase));

                                    if (willReadBodyAttribute == null)
                                        await AddBodyParameterAsync(bodyParameterName, parameter, context.OperationDescription.Operation, context.SwaggerGenerator).ConfigureAwait(false);
                                    else
                                    {
                                        // Try to get a boolean property value from the attribute which explicity tells us whether to read from the body
                                        // If no such property exists, then default to false since WebAPI's HttpParameterBinding.WillReadBody defaults to false
                                        var willReadBody = willReadBodyAttribute.TryGetPropertyValue("WillReadBody", true);
                                        if (willReadBody)
                                            await AddBodyParameterAsync(bodyParameterName, parameter, context.OperationDescription.Operation, context.SwaggerGenerator).ConfigureAwait(false);

                                        // If we are not reading from the body, then treat this as a primitive.
                                        // This may seem odd, but it allows for primitive -> custom complex-type bindings which are very common
                                        // In this case, the API author should use a TypeMapper to define the parameter
                                        else
                                            await AddPrimitiveParameterAsync(uriParameterName, context.OperationDescription.Operation, parameter, context.SwaggerGenerator).ConfigureAwait(false);
                                    }
                                }
                                else if (fromBodyAttribute != null || (fromUriAttribute == null && _settings.IsAspNetCore == false))
                                    await AddBodyParameterAsync(bodyParameterName, parameter, context.OperationDescription.Operation, context.SwaggerGenerator).ConfigureAwait(false);
                                else
                                    await AddPrimitiveParametersFromUriAsync(httpPath, uriParameterName, context.OperationDescription.Operation, parameter, parameterInfo, context.SwaggerGenerator).ConfigureAwait(false);
                            }
                            else
                            {
                                if (fromBodyAttribute != null)
                                    await AddBodyParameterAsync(bodyParameterName, parameter, context.OperationDescription.Operation, context.SwaggerGenerator).ConfigureAwait(false);
                                else
                                    await AddPrimitiveParameterAsync(uriParameterName, context.OperationDescription.Operation, parameter, context.SwaggerGenerator).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            if (_settings.AddMissingPathParameters)
            {
                foreach (Match match in Regex.Matches(httpPath, "{(.*?)(:(([^/]*)?))?}"))
                {
                    var parameterName = match.Groups[1].Value;
                    if (context.OperationDescription.Operation.Parameters.All(p => !string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var parameterType = match.Groups.Count == 5 ? match.Groups[3].Value : "string";
                        var operationParameter = context.SwaggerGenerator.CreatePathParameter(parameterName, parameterType);
                        context.OperationDescription.Operation.Parameters.Add(operationParameter);
                    }
                }
            }

            RemoveUnusedPathParameters(context.OperationDescription, httpPath);
            UpdateConsumedTypes(context.OperationDescription);

            EnsureSingleBodyParameter(context.OperationDescription);

            return true;
        }

        private void EnsureSingleBodyParameter(SwaggerOperationDescription operationDescription)
        {
            if (operationDescription.Operation.ActualParameters.Count(p => p.Kind == SwaggerParameterKind.Body) > 1)
                throw new InvalidOperationException("The operation '" + operationDescription.Operation.OperationId + "' has more than one body parameter.");
        }

        private void UpdateConsumedTypes(SwaggerOperationDescription operationDescription)
        {
            if (operationDescription.Operation.ActualParameters.Any(p => p.Type == JsonObjectType.File))
                operationDescription.Operation.Consumes = new List<string> { "multipart/form-data" };
        }

        private void RemoveUnusedPathParameters(SwaggerOperationDescription operationDescription, string httpPath)
        {
            operationDescription.Path = Regex.Replace(httpPath, "{(.*?)(:(([^/]*)?))?}", match =>
            {
                var parameterName = match.Groups[1].Value.TrimEnd('?');
                if (operationDescription.Operation.ActualParameters.Any(p => p.Kind == SwaggerParameterKind.Path && string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase)))
                    return "{" + parameterName + "}";
                return string.Empty;
            }).TrimEnd('/');
        }

        private async Task<bool> TryAddFileParameterAsync(JsonObjectTypeDescription info, SwaggerOperation operation, ParameterInfo parameter, SwaggerGenerator swaggerGenerator)
        {
            var isFileArray = IsFileArray(parameter.ParameterType, info);
            if (info.Type == JsonObjectType.File || isFileArray)
            {
                await AddFileParameterAsync(parameter, isFileArray, operation, swaggerGenerator).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private async Task AddFileParameterAsync(ParameterInfo parameter, bool isFileArray, SwaggerOperation operation, SwaggerGenerator swaggerGenerator)
        {
            var attributes = parameter.GetCustomAttributes().ToList();

            // TODO: Check if there is a way to control the property name
            var parameterDocumentation = await parameter.GetDescriptionAsync(parameter.GetCustomAttributes()).ConfigureAwait(false);
            var operationParameter = await swaggerGenerator.CreatePrimitiveParameterAsync(parameter.Name, parameterDocumentation, parameter.ParameterType, attributes).ConfigureAwait(false);

            InitializeFileParameter(operationParameter, isFileArray);
            operation.Parameters.Add(operationParameter);
        }

        private bool IsFileArray(Type type, JsonObjectTypeDescription typeInfo)
        {
            var isFormFileCollection = type.Name == "IFormFileCollection";
            var isFileArray = typeInfo.Type == JsonObjectType.Array && type.GenericTypeArguments.Any() &&
                JsonObjectTypeDescription.FromType(type.GenericTypeArguments[0], _settings.ResolveContract(type.GenericTypeArguments[0]), null, _settings.DefaultEnumHandling).Type == JsonObjectType.File;
            return isFormFileCollection || isFileArray;
        }

        private async Task AddBodyParameterAsync(string name, ParameterInfo parameter, SwaggerOperation operation, SwaggerGenerator swaggerGenerator)
        {
            if (parameter.ParameterType.Name == "XmlDocument" || parameter.ParameterType.InheritsFrom("XmlDocument", TypeNameStyle.Name))
            {
                operation.Consumes = new List<string> { "application/xml" };
                operation.Parameters.Add(new SwaggerParameter
                {
                    Name = name,
                    Kind = SwaggerParameterKind.Body,
                    IsRequired = parameter.HasDefaultValue == false,
                    IsNullableRaw = true,
                    Description = await parameter.GetDescriptionAsync(parameter.GetCustomAttributes()).ConfigureAwait(false)
                });
            }
            else
            {
                var operationParameter = await swaggerGenerator.CreateBodyParameterAsync(name, parameter).ConfigureAwait(false);
                operation.Parameters.Add(operationParameter);
            }
        }

        private async Task AddPrimitiveParametersFromUriAsync(string httpPath, string name, SwaggerOperation operation, ParameterInfo parameter, JsonObjectTypeDescription typeDescription, SwaggerGenerator swaggerGenerator)
        {
            if (typeDescription.Type.HasFlag(JsonObjectType.Array))
            {
                var parameterDocumentation = await parameter.GetDescriptionAsync(parameter.GetCustomAttributes()).ConfigureAwait(false);
                var operationParameter = await swaggerGenerator.CreatePrimitiveParameterAsync(name, parameterDocumentation,
                    parameter.ParameterType, parameter.GetCustomAttributes().ToList()).ConfigureAwait(false);

                operationParameter.Kind = SwaggerParameterKind.Query;
                operation.Parameters.Add(operationParameter);
            }
            else
            {
                foreach (var property in parameter.ParameterType.GetRuntimeProperties())
                {
                    var attributes = property.GetCustomAttributes().ToList();
                    if (attributes.All(a => a.GetType().Name != "SwaggerIgnoreAttribute" && a.GetType().Name != "JsonIgnoreAttribute"))
                    {
                        var fromQueryAttribute = attributes.SingleOrDefault(a => a.GetType().Name == "FromQueryAttribute");
                        var propertyName = TryGetStringPropertyValue(fromQueryAttribute, "Name") ?? JsonReflectionUtilities.GetPropertyName(property, _settings.DefaultPropertyNameHandling);

                        dynamic fromRouteAttribute = attributes.SingleOrDefault(a => a.GetType().FullName == "Microsoft.AspNetCore.Mvc.FromRouteAttribute");
                        if (fromRouteAttribute != null && !string.IsNullOrEmpty(fromRouteAttribute?.Name))
                            propertyName = fromRouteAttribute?.Name;

                        dynamic fromHeaderAttribute = attributes.SingleOrDefault(a => a.GetType().FullName == "Microsoft.AspNetCore.Mvc.FromHeaderAttribute");
                        if (fromHeaderAttribute != null && !string.IsNullOrEmpty(fromHeaderAttribute?.Name))
                            propertyName = fromHeaderAttribute?.Name;

                        var propertySummary = await property.GetXmlSummaryAsync().ConfigureAwait(false);
                        var operationParameter = await swaggerGenerator.CreatePrimitiveParameterAsync(propertyName, propertySummary, property.PropertyType, attributes).ConfigureAwait(false);

                        // TODO: Check if required can be controlled with mechanisms other than RequiredAttribute

                        var parameterInfo = JsonObjectTypeDescription.FromType(property.PropertyType, _settings.ResolveContract(property.PropertyType), attributes, _settings.DefaultEnumHandling);
                        var isFileArray = IsFileArray(property.PropertyType, parameterInfo);
                        if (parameterInfo.Type == JsonObjectType.File || isFileArray)
                            InitializeFileParameter(operationParameter, isFileArray);
                        else if (fromRouteAttribute != null
                            || httpPath.ToLowerInvariant().Contains("{" + propertyName.ToLower() + "}")
                            || httpPath.ToLowerInvariant().Contains("{" + propertyName.ToLower() + ":"))
                        {
                            operationParameter.Kind = SwaggerParameterKind.Path;
                            operationParameter.IsNullableRaw = false;
                            operationParameter.IsRequired = true; // Path is always required => property not needed
                        }
                        else if (fromHeaderAttribute != null)
                            operationParameter.Kind = SwaggerParameterKind.Header;
                        else
                            operationParameter.Kind = SwaggerParameterKind.Query;

                        operation.Parameters.Add(operationParameter);
                    }
                }
            }
        }

        private async Task AddPrimitiveParameterAsync(string name, SwaggerOperation operation, ParameterInfo parameter, SwaggerGenerator swaggerGenerator)
        {
            var operationParameter = await swaggerGenerator.CreatePrimitiveParameterAsync(name, parameter).ConfigureAwait(false);
            operationParameter.Kind = SwaggerParameterKind.Query;
            operationParameter.IsRequired = operationParameter.IsRequired || parameter.HasDefaultValue == false;

            if (parameter.HasDefaultValue)
                operationParameter.Default = parameter.DefaultValue;

            operation.Parameters.Add(operationParameter);
        }

        private void InitializeFileParameter(SwaggerParameter operationParameter, bool isFileArray)
        {
            operationParameter.Type = JsonObjectType.File;
            operationParameter.Kind = SwaggerParameterKind.FormData;

            if (isFileArray)
                operationParameter.CollectionFormat = SwaggerParameterCollectionFormat.Multi;
        }

        private string TryGetStringPropertyValue(dynamic obj, string propertyName)
        {
            return ((object)obj)?.GetType().GetRuntimeProperty(propertyName) != null && !string.IsNullOrEmpty(obj.Name) ? obj.Name : null;
        }
    }
}
