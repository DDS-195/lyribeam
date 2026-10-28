package org.json;
import java.util.*;
/** Flat JSONObject host fixture for the production source selector and frame encoder. */
public class JSONObject {
 private final Map<String,Object> values=new LinkedHashMap<>();
 public JSONObject put(String key,Object value){values.put(key,value);return this;}
 public Object opt(String key){return values.get(key);}
 public String optString(String key){Object value=values.get(key);return value==null?"":String.valueOf(value);}
 public long optLong(String key){Object value=values.get(key);if(value instanceof Number)return ((Number)value).longValue();try{return Long.parseLong(String.valueOf(value));}catch(Exception ignored){return 0;}}
 public boolean optBoolean(String key){Object value=values.get(key);return value instanceof Boolean?(Boolean)value:"true".equalsIgnoreCase(String.valueOf(value));}
 public boolean has(String key){return values.containsKey(key);}
 public Object remove(String key){return values.remove(key);}
 public Iterator<String> keys(){return new ArrayList<>(values.keySet()).iterator();}
 public int length(){return values.size();}
 public String toString(){StringBuilder out=new StringBuilder("{");boolean first=true;for(Map.Entry<String,Object> entry:values.entrySet()){if(!first)out.append(',');first=false;out.append(quote(entry.getKey())).append(':');Object value=entry.getValue();out.append(value==null?"null":value instanceof Number||value instanceof Boolean?String.valueOf(value):quote(String.valueOf(value)));}return out.append('}').toString();}
 private static String quote(String value){StringBuilder out=new StringBuilder("\"");for(int i=0;i<value.length();i++){char c=value.charAt(i);switch(c){case '"':out.append("\\\"");break;case '\\':out.append("\\\\");break;case '\n':out.append("\\n");break;case '\r':out.append("\\r");break;case '\t':out.append("\\t");break;default:if(c<32){String hex=Integer.toHexString(c);out.append("\\u");for(int j=hex.length();j<4;j++)out.append('0');out.append(hex);}else out.append(c);}}return out.append('"').toString();}
}
