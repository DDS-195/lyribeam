package android.net;public class Uri{public final String value;private Uri(String value){this.value=value;}public static Uri parse(String value){return new Uri(value);}}
