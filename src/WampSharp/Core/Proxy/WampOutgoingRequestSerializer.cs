using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reflection;
using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
using WampSharp.Core.Contracts;
using WampSharp.Core.Message;
using WampSharp.Core.Serialization;
using Expression = System.Linq.Expressions.Expression;

namespace WampSharp.Core.Proxy
{
    /// <summary>
    /// An implementation of <see cref="IWampOutgoingRequestSerializer{TMessage}"/>.
    /// </summary>
    public class WampOutgoingRequestSerializer<TMessage> : IWampOutgoingRequestSerializer<TMessage>
    {
        private readonly IWampFormatter<TMessage> mFormatter;

        private static readonly ConcurrentDictionary<MethodInfo, Func<object[], IWampFormatter<TMessage>, WampMessage<TMessage>>> mSerializers = new ConcurrentDictionary<MethodInfo, Func<object[], IWampFormatter<TMessage>, WampMessage<TMessage>>>(); 

        /// <summary>
        /// Initializes a new instance of <see cref="WampOutgoingRequestSerializer{TMessage}"/>.
        /// </summary>
        /// <param name="formatter">The <see cref="IWampFormatter{TMessage}"/> to
        /// serialize arguments with.</param>
        public WampOutgoingRequestSerializer(IWampFormatter<TMessage> formatter)
        {
            mFormatter = formatter;
        }

        public WampMessage<TMessage> SerializeRequest(MethodInfo method, object[] arguments)
        {
            return mSerializers.GetOrAdd(method, GetSerializer)(arguments, mFormatter);
            /*
            WampHandlerAttribute attribute = mMethodToHandlerAttribute.GetOrAdd(method,
                m => m.GetCustomAttribute<WampHandlerAttribute>(true));

            WampMessageType messageType = attribute.MessageType;

            WampMessage<TMessage> result = new WampMessage<TMessage>()
                                               {
                                                   MessageType = messageType
                                               };

            var parameters = method.GetParameters()
                                   .Zip(arguments,
                                        (parameterInfo, argument) =>
                                        new
                                            {
                                                parameterInfo,
                                                argument
                                            })
                                   .Where(x => !x.parameterInfo.IsDefined(typeof(WampProxyParameterAttribute), true));

            List<TMessage> messageArguments = new List<TMessage>();

            foreach (var parameter in parameters)
            {
                if (!parameter.parameterInfo.IsDefined(typeof(ParamArrayAttribute), true))
                {
                    TMessage serialized = mFormatter.Serialize(parameter.argument);
                    messageArguments.Add(serialized);
                }
                else
                {
                    object[] paramsArray = parameter.argument as object[];

                    foreach (object param in paramsArray)
                    {
                        TMessage serialized = mFormatter.Serialize(param);
                        messageArguments.Add(serialized);
                    }
                }
            }

            result.Arguments = messageArguments.ToArray();

            return result;*/
        }

        private static Func<object[], IWampFormatter<TMessage>,  WampMessage<TMessage>> GetSerializer(MethodInfo methodInfo)
        {
            // Get message type
            WampHandlerAttribute handlerAttribute = methodInfo.GetCustomAttribute<WampHandlerAttribute>(true);
            WampMessageType type = handlerAttribute.MessageType;

            // Get method parameters
            var parameters =
                methodInfo.GetParameters()
                    .Select((p, i) => new {Index = i, Parameter = p, ParamArray = p.IsDefined(typeof(ParamArrayAttribute), true)})
                    .Where(p => !p.Parameter.IsDefined(typeof (WampProxyParameterAttribute), true))
                    .Select((p, i) => new { OldIndex = p.Index, NewIndex = i, p.Parameter, p.ParamArray })
                    .ToList();

            var lastParameter = parameters.Last();

            ParameterExpression objectArrayParameter = Expression.Parameter(typeof (object[]));
            ParameterExpression formatterParameter = Expression.Parameter(typeof (IWampFormatter<TMessage>));
            ParameterExpression argumentsVariable = Expression.Variable(typeof (TMessage[]));
            ParameterExpression returnValue = Expression.Variable(typeof (WampMessage<TMessage>));
            IList<Expression> methodBody = new List<Expression>();
            IList<ParameterExpression> variables = new List<ParameterExpression> { argumentsVariable, returnValue };

            Expression retValLengthExpression;

            ParameterExpression paramArrayValue = Expression.Parameter(lastParameter.Parameter.ParameterType);
            
            if (lastParameter.ParamArray)
            {
                variables.Add(paramArrayValue);

                methodBody.Add(
                    Expression.Assign(paramArrayValue,
                        Expression.Convert(
                            Expression.ArrayIndex(objectArrayParameter, Expression.Constant(lastParameter.OldIndex)),
                            paramArrayValue.Type)));

                retValLengthExpression = Expression.Add(Expression.Constant(parameters.Count - 1),
                    Expression.ArrayLength(paramArrayValue));
            }
            else
            {
                retValLengthExpression = Expression.Constant(parameters.Count);
            }

            methodBody.Add(Expression.Assign(argumentsVariable, Expression.NewArrayBounds(typeof(TMessage), retValLengthExpression)));

            foreach (var p in parameters.Where(p => !p.ParamArray))
            {
                methodBody.Add(Expression.Assign(Expression.ArrayAccess(argumentsVariable, Expression.Constant(p.NewIndex)),
                    Expression.Call(formatterParameter, "Serialize", null, Expression.ArrayIndex(objectArrayParameter, Expression.Constant(p.OldIndex)))));
            }

            if (lastParameter.ParamArray)
            {
                ParameterExpression i = Expression.Parameter(typeof (int), "i");
                ParameterExpression count = Expression.Parameter(typeof(int), "count");
                variables.Add(i);
                variables.Add(count);

                methodBody.Add(Expression.Assign(i, Expression.Constant(0)));
                methodBody.Add(Expression.Assign(count, Expression.ArrayLength(paramArrayValue)));

                LabelTarget breakLabel = Expression.Label();
                LabelTarget continueLabel = Expression.Label();

                methodBody.Add(Expression.Loop(Expression.Block(
                    Expression.IfThen(Expression.MakeBinary(ExpressionType.GreaterThanOrEqual, i, count), Expression.Goto(breakLabel)),
                    Expression.Assign(Expression.ArrayAccess(argumentsVariable, Expression.Add(Expression.Constant(lastParameter.NewIndex), i)), Expression.Call(formatterParameter, "Serialize", null, Expression.ArrayIndex(paramArrayValue, i))),
                    Expression.PostIncrementAssign(i),
                    Expression.Goto(continueLabel)
                    ), breakLabel, continueLabel));

            }


            methodBody.Add(Expression.Assign(returnValue, Expression.New(typeof(WampMessage<TMessage>))));
            methodBody.Add(Expression.Assign(Expression.Property(returnValue, "MessageType"), Expression.Constant(type)));
            methodBody.Add(Expression.Assign(Expression.Property(returnValue, "Arguments"), argumentsVariable));

            methodBody.Add(returnValue);

            var lambdaExpression = Expression.Lambda<Func<object[], IWampFormatter<TMessage>, WampMessage<TMessage>>>(
                Expression.Block(variables, methodBody), objectArrayParameter, formatterParameter);

            return lambdaExpression.Compile();


        }
    }
}