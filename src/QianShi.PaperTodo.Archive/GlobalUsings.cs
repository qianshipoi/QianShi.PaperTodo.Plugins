// The plugin lives in the QianShi.PaperTodo.* namespace family, so an unqualified "PaperTodo.Plugin"
// inside those namespaces resolves relative to QianShi and fails. One global using keeps every file
// on the host contract without repeating the trick.
global using PaperTodo.Plugin;
