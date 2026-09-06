using System.Runtime.CompilerServices;

// The package's own test assemblies (Tests/Editor and Tests/Runtime) reach internals such as HtmlEvent.Parse,
// HtmlMessage's constructor and the image encoder.
[assembly: InternalsVisibleTo("Hiccup.Editor.Tests")]
[assembly: InternalsVisibleTo("Hiccup.Tests")]
