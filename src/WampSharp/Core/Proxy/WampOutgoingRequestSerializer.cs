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

            // Get the last parameter (Might have a "ParamsAttribute")
            var lastParameter = parameters.Last();

            // Function Arguments
            ParameterExpression objectArrayParameter = Expression.Parameter(typeof (object[]));
            ParameterExpression formatterParameter = Expression.Parameter(typeof (IWampFormatter<TMessage>));
            
            // Variables
            ParameterExpression argumentsVariable = Expression.Variable(typeof (TMessage[]));
            ParameterExpression returnValue = Expression.Variable(typeof (WampMessage<TMessage>));
            IList<ParameterExpression> variables = new List<ParameterExpression> { argumentsVariable, returnValue };

            // Method Body
            IList<Expression> methodBody = new List<Expression>();

            Expression retValLengthExpression;

            ParameterExpression paramArrayValue = Expression.Parameter(lastParameter.Parameter.ParameterType);
            
            // If the last attribute is a params, we need to 'flatten' it into the returned arguments
            if (lastParameter.ParamArray)
            {
                // Add a object[] variable
                variables.Add(paramArrayValue);

                // And set the value
                methodBody.Add(
                    Expression.Assign(paramArrayValue,
                        Expression.Convert(
                            Expression.ArrayIndex(objectArrayParameter, Expression.Constant(lastParameter.OldIndex)),
                            paramArrayValue.Type)));

                // Setting the returned arguments length
                retValLengthExpression = Expression.Add(Expression.Constant(parameters.Count - 1),
                    Expression.ArrayLength(paramArrayValue));
            }
            else
            {
                retValLengthExpression = Expression.Constant(parameters.Count);
            }

            // Creating new array for returned arguments
            methodBody.Add(Expression.Assign(argumentsVariable, Expression.NewArrayBounds(typeof(TMessage), retValLengthExpression)));

            // Serializing all the values except the param arrays
            foreach (var p in parameters.Where(p => !p.ParamArray))
            {
                methodBody.Add(Expression.Assign(Expression.ArrayAccess(argumentsVariable, Expression.Constant(p.NewIndex)),
                    Expression.Call(formatterParameter, "Serialize", null, Expression.ArrayIndex(objectArrayParameter, Expression.Constant(p.OldIndex)))));
            }

            // Flatting the param array value to the returned arguments
            if (lastParameter.ParamArray)
            {
                // Creating i and count variables
                ParameterExpression i = Expression.Parameter(typeof (int), "i");
                ParameterExpression count = Expression.Parameter(typeof(int), "count");
                
                variables.Add(i);
                variables.Add(count);

                // i = 0, count = lastParameter.Length
                methodBody.Add(Expression.Assign(i, Expression.Constant(0)));
                methodBody.Add(Expression.Assign(count, Expression.ArrayLength(paramArrayValue)));

                LabelTarget breakLabel = Expression.Label();
                LabelTarget continueLabel = Expression.Label();

                // Adding loop
                methodBody.Add(Expression.Loop(Expression.Block(
                    Expression.IfThen(Expression.MakeBinary(ExpressionType.GreaterThanOrEqual, i, count),
                        Expression.Goto(breakLabel)),
                    Expression.Assign(
                        Expression.ArrayAccess(argumentsVariable,
                            Expression.Add(Expression.Constant(lastParameter.NewIndex), i)),
                        Expression.Call(formatterParameter, "Serialize", null, Expression.ArrayIndex(paramArrayValue, i))),
                    Expression.PostIncrementAssign(i),
                    Expression.Goto(continueLabel)
                    ), breakLabel, continueLabel));
            }

            // Creating the return value
            methodBody.Add(Expression.Assign(returnValue, Expression.New(typeof(WampMessage<TMessage>))));
            methodBody.Add(Expression.Assign(Expression.Property(returnValue, "MessageType"), Expression.Constant(type)));
            methodBody.Add(Expression.Assign(Expression.Property(returnValue, "Arguments"), argumentsVariable));

            // Returning the value (Last expression in method body is the returned value, same as ruby)
            methodBody.Add(returnValue);

            // Compiling the method
            var lambdaExpression = Expression.Lambda<Func<object[], IWampFormatter<TMessage>, WampMessage<TMessage>>>(
                Expression.Block(variables, methodBody), objectArrayParameter, formatterParameter);

            return lambdaExpression.Compile();


        }
    }
}