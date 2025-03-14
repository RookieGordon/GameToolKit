﻿/*
 * author       : Gordon
 * datetime     : 2025/3/14
 * description  : 深拷贝工具，参考：https://www.cnblogs.com/SF8588/p/16152078.html
 */

using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ToolKit.Tools
{
    public static class TransObjHelper<TIn, TOut> where TIn : new() where TOut : new()
    {
        private static readonly Func<TIn, TOut> cache = GetFunc();

        private static Func<TIn, TOut> GetFunc()
        {
            ParameterExpression parameterExpression = Expression.Parameter(typeof(TIn), "p");
            List<MemberBinding> memberBindingList = new List<MemberBinding>();

            foreach (var item in typeof(TOut).GetProperties())
            {
                if (!item.CanWrite)
                {
                    continue;
                }

                MemberExpression property =
                    Expression.Property(parameterExpression, typeof(TIn).GetProperty(item.Name));
                MemberBinding memberBinding = Expression.Bind(item, property);
                memberBindingList.Add(memberBinding);
            }

            MemberInitExpression memberInitExpression =
                Expression.MemberInit(Expression.New(typeof(TOut)), memberBindingList.ToArray());
            Expression<Func<TIn, TOut>> lambda = Expression.Lambda<Func<TIn, TOut>>(memberInitExpression,
                new ParameterExpression[] { parameterExpression });

            return lambda.Compile();
        }

        public static TOut Trans(TIn tIn)
        {
            return cache(tIn);
        }
    }
}