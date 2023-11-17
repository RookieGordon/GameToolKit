// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.MiniJSON
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  public static class MiniJSON
  {
    public static object Deserialize(string json) => json == null ? (object) null : MiniJSON.Parser.Parse(json);

    public static string Serialize(object obj) => MiniJSON.Serializer.Serialize(obj);

    private sealed class Parser : IDisposable
    {
      private const string WORD_BREAK = "{}[],:\"";
      private StringReader json;

      private Parser(string jsonString) => this.json = new StringReader(jsonString);

      public static bool IsWordBreak(char c) => char.IsWhiteSpace(c) || "{}[],:\"".IndexOf(c) != -1;

      public static object Parse(string jsonString)
      {
        using (MiniJSON.Parser parser = new MiniJSON.Parser(jsonString))
          return parser.ParseValue();
      }

      public void Dispose()
      {
        this.json.Dispose();
        this.json = (StringReader) null;
      }

      private Dictionary<string, object> ParseObject()
      {
        Dictionary<string, object> dictionary = new Dictionary<string, object>();
        this.json.Read();
        while (true)
        {
          MiniJSON.Parser.TOKEN nextToken;
          do
          {
            nextToken = this.NextToken;
            switch (nextToken)
            {
              case MiniJSON.Parser.TOKEN.NONE:
                goto label_3;
              case MiniJSON.Parser.TOKEN.CURLY_CLOSE:
                goto label_4;
              default:
                continue;
            }
          }
          while (nextToken == MiniJSON.Parser.TOKEN.COMMA);
          string key = this.ParseString();
          if (key != null)
          {
            if (this.NextToken == MiniJSON.Parser.TOKEN.COLON)
            {
              this.json.Read();
              dictionary[key] = this.ParseValue();
            }
            else
              goto label_8;
          }
          else
            goto label_6;
        }
label_3:
        return (Dictionary<string, object>) null;
label_4:
        return dictionary;
label_6:
        return (Dictionary<string, object>) null;
label_8:
        return (Dictionary<string, object>) null;
      }

      private List<object> ParseArray()
      {
        List<object> array = new List<object>();
        this.json.Read();
        bool flag = true;
        while (flag)
        {
          MiniJSON.Parser.TOKEN nextToken = this.NextToken;
          switch (nextToken)
          {
            case MiniJSON.Parser.TOKEN.SQUARED_CLOSE:
              flag = false;
              continue;
            case MiniJSON.Parser.TOKEN.COMMA:
              continue;
            default:
              if (nextToken == MiniJSON.Parser.TOKEN.NONE)
                return (List<object>) null;
              object byToken = this.ParseByToken(nextToken);
              array.Add(byToken);
              continue;
          }
        }
        return array;
      }

      private object ParseValue() => this.ParseByToken(this.NextToken);

      private object ParseByToken(MiniJSON.Parser.TOKEN token)
      {
        switch (token)
        {
          case MiniJSON.Parser.TOKEN.CURLY_OPEN:
            return (object) this.ParseObject();
          case MiniJSON.Parser.TOKEN.SQUARED_OPEN:
            return (object) this.ParseArray();
          case MiniJSON.Parser.TOKEN.STRING:
            return (object) this.ParseString();
          case MiniJSON.Parser.TOKEN.NUMBER:
            return this.ParseNumber();
          case MiniJSON.Parser.TOKEN.INFINITY:
            return (object) float.PositiveInfinity;
          case MiniJSON.Parser.TOKEN.NEGINFINITY:
            return (object) float.NegativeInfinity;
          case MiniJSON.Parser.TOKEN.TRUE:
            return (object) true;
          case MiniJSON.Parser.TOKEN.FALSE:
            return (object) false;
          case MiniJSON.Parser.TOKEN.NULL:
            return (object) null;
          default:
            return (object) null;
        }
      }

      private string ParseString()
      {
        StringBuilder stringBuilder = new StringBuilder();
        this.json.Read();
        bool flag = true;
        while (flag)
        {
          if (this.json.Peek() == -1)
            break;
          char nextChar1 = this.NextChar;
          switch (nextChar1)
          {
            case '"':
              flag = false;
              continue;
            case '\\':
              if (this.json.Peek() == -1)
              {
                flag = false;
                continue;
              }
              char nextChar2 = this.NextChar;
              switch (nextChar2)
              {
                case 'r':
                  stringBuilder.Append('\r');
                  continue;
                case 't':
                  stringBuilder.Append('\t');
                  continue;
                case 'u':
                  char[] chArray = new char[4];
                  for (int index = 0; index < 4; ++index)
                    chArray[index] = this.NextChar;
                  stringBuilder.Append((char) Convert.ToInt32(new string(chArray), 16));
                  continue;
                default:
                  if (nextChar2 != '"' && nextChar2 != '/' && nextChar2 != '\\')
                  {
                    switch (nextChar2)
                    {
                      case 'b':
                        stringBuilder.Append('\b');
                        continue;
                      case 'f':
                        stringBuilder.Append('\f');
                        continue;
                      case 'n':
                        stringBuilder.Append('\n');
                        continue;
                      default:
                        continue;
                    }
                  }
                  else
                  {
                    stringBuilder.Append(nextChar2);
                    continue;
                  }
              }
            default:
              stringBuilder.Append(nextChar1);
              continue;
          }
        }
        return stringBuilder.ToString();
      }

      private object ParseNumber()
      {
        string nextWord = this.NextWord;
        if (nextWord.IndexOf('.') == -1)
        {
          long result;
          long.TryParse(nextWord, NumberStyles.Any, (IFormatProvider) CultureInfo.InvariantCulture, out result);
          return (object) result;
        }
        double result1;
        double.TryParse(nextWord, NumberStyles.Any, (IFormatProvider) CultureInfo.InvariantCulture, out result1);
        return (object) result1;
      }

      private void EatWhitespace()
      {
        while (char.IsWhiteSpace(this.PeekChar))
        {
          this.json.Read();
          if (this.json.Peek() == -1)
            break;
        }
      }

      private char PeekChar => Convert.ToChar(this.json.Peek());

      private char NextChar => Convert.ToChar(this.json.Read());

      private string NextWord
      {
        get
        {
          StringBuilder stringBuilder = new StringBuilder();
          while (!MiniJSON.Parser.IsWordBreak(this.PeekChar))
          {
            stringBuilder.Append(this.NextChar);
            if (this.json.Peek() == -1)
              break;
          }
          return stringBuilder.ToString();
        }
      }

      private MiniJSON.Parser.TOKEN NextToken
      {
        get
        {
          this.EatWhitespace();
          if (this.json.Peek() == -1)
            return MiniJSON.Parser.TOKEN.NONE;
          char peekChar = this.PeekChar;
          switch (peekChar)
          {
            case ',':
              this.json.Read();
              return MiniJSON.Parser.TOKEN.COMMA;
            case '-':
            case '0':
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
            case '8':
            case '9':
              return MiniJSON.Parser.TOKEN.NUMBER;
            case ':':
              return MiniJSON.Parser.TOKEN.COLON;
            default:
              switch (peekChar)
              {
                case '[':
                  return MiniJSON.Parser.TOKEN.SQUARED_OPEN;
                case ']':
                  this.json.Read();
                  return MiniJSON.Parser.TOKEN.SQUARED_CLOSE;
                default:
                  switch (peekChar)
                  {
                    case '{':
                      return MiniJSON.Parser.TOKEN.CURLY_OPEN;
                    case '}':
                      this.json.Read();
                      return MiniJSON.Parser.TOKEN.CURLY_CLOSE;
                    default:
                      if (peekChar == '"')
                        return MiniJSON.Parser.TOKEN.STRING;
                      switch (this.NextWord)
                      {
                        case "false":
                          return MiniJSON.Parser.TOKEN.FALSE;
                        case "true":
                          return MiniJSON.Parser.TOKEN.TRUE;
                        case "null":
                          return MiniJSON.Parser.TOKEN.NULL;
                        case "Infinity":
                          return MiniJSON.Parser.TOKEN.INFINITY;
                        case "-Infinity":
                          return MiniJSON.Parser.TOKEN.NEGINFINITY;
                        default:
                          return MiniJSON.Parser.TOKEN.NONE;
                      }
                  }
              }
          }
        }
      }

      private enum TOKEN
      {
        NONE,
        CURLY_OPEN,
        CURLY_CLOSE,
        SQUARED_OPEN,
        SQUARED_CLOSE,
        COLON,
        COMMA,
        STRING,
        NUMBER,
        INFINITY,
        NEGINFINITY,
        TRUE,
        FALSE,
        NULL,
      }
    }

    private sealed class Serializer
    {
      private StringBuilder builder;

      private Serializer() => this.builder = new StringBuilder();

      public static string Serialize(object obj)
      {
        MiniJSON.Serializer serializer = new MiniJSON.Serializer();
        serializer.SerializeValue(obj);
        return serializer.builder.ToString();
      }

      private void SerializeValue(object value)
      {
        switch (value)
        {
          case null:
            this.builder.Append("null");
            break;
          case string str:
            this.SerializeString(str);
            break;
          case bool flag:
            this.builder.Append(!flag ? "false" : "true");
            break;
          case IList anArray:
            this.SerializeArray(anArray);
            break;
          case IDictionary dictionary:
            this.SerializeObject(dictionary);
            break;
          case char c:
            this.SerializeString(new string(c, 1));
            break;
          default:
            this.SerializeOther(value);
            break;
        }
      }

      private void SerializeObject(IDictionary obj)
      {
        bool flag = true;
        this.builder.Append('{');
        foreach (object key in (IEnumerable) obj.Keys)
        {
          if (!flag)
            this.builder.Append(',');
          this.SerializeString(key.ToString());
          this.builder.Append(':');
          this.SerializeValue(obj[key]);
          flag = false;
        }
        this.builder.Append('}');
      }

      private void SerializeArray(IList anArray)
      {
        this.builder.Append('[');
        bool flag = true;
        for (int index = 0; index < anArray.Count; ++index)
        {
          object an = anArray[index];
          if (!flag)
            this.builder.Append(',');
          this.SerializeValue(an);
          flag = false;
        }
        this.builder.Append(']');
      }

      private void SerializeString(string str)
      {
        this.builder.Append('"');
        foreach (char ch in str.ToCharArray())
        {
          switch (ch)
          {
            case '\b':
              this.builder.Append("\\b");
              break;
            case '\t':
              this.builder.Append("\\t");
              break;
            case '\n':
              this.builder.Append("\\n");
              break;
            case '\f':
              this.builder.Append("\\f");
              break;
            case '\r':
              this.builder.Append("\\r");
              break;
            default:
              switch (ch)
              {
                case '"':
                  this.builder.Append("\\\"");
                  continue;
                case '\\':
                  this.builder.Append("\\\\");
                  continue;
                default:
                  int int32 = Convert.ToInt32(ch);
                  if (int32 >= 32 && int32 <= 126)
                  {
                    this.builder.Append(ch);
                    continue;
                  }
                  this.builder.Append("\\u");
                  this.builder.Append(int32.ToString("x4"));
                  continue;
              }
          }
        }
        this.builder.Append('"');
      }

      private void SerializeOther(object value)
      {
        switch (value)
        {
          case float num:
            this.builder.Append(num.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture));
            break;
          case int _:
          case uint _:
          case long _:
          case sbyte _:
          case byte _:
          case short _:
          case ushort _:
          case ulong _:
            this.builder.Append(value);
            break;
          case double _:
          case Decimal _:
            this.builder.Append(Convert.ToDouble(value).ToString("R", (IFormatProvider) CultureInfo.InvariantCulture));
            break;
          case Vector2 vector2:
            this.builder.Append("\"(" + vector2.x.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector2.y.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + ")\"");
            break;
          case Vector3 vector3:
            this.builder.Append("\"(" + vector3.x.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector3.y.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector3.z.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + ")\"");
            break;
          case Vector4 vector4:
            this.builder.Append("\"(" + vector4.x.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector4.y.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector4.z.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + vector4.w.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + ")\"");
            break;
          case Quaternion quaternion:
            this.builder.Append("\"(" + quaternion.x.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + quaternion.y.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + quaternion.z.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + "," + quaternion.w.ToString("R", (IFormatProvider) CultureInfo.InvariantCulture) + ")\"");
            break;
          default:
            this.SerializeString(value.ToString());
            break;
        }
      }
    }
  }
}
