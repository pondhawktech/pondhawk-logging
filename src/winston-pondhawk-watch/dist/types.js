// Microsoft.Extensions.Logging's names, as Watch uses. The old Serilog names stay as aliases until the next
// major version; each comes before its replacement so the reverse mapping (Level[0]) gives the new name.
export var Level;
(function (Level) {
    /** @deprecated Use Level.Trace. */
    Level[Level["Verbose"] = 0] = "Verbose";
    Level[Level["Trace"] = 0] = "Trace";
    Level[Level["Debug"] = 1] = "Debug";
    Level[Level["Information"] = 2] = "Information";
    Level[Level["Warning"] = 3] = "Warning";
    Level[Level["Error"] = 4] = "Error";
    /** @deprecated Use Level.Critical. */
    Level[Level["Fatal"] = 5] = "Fatal";
    Level[Level["Critical"] = 5] = "Critical";
})(Level || (Level = {}));
export var PayloadType;
(function (PayloadType) {
    PayloadType[PayloadType["None"] = 0] = "None";
    PayloadType[PayloadType["Json"] = 1] = "Json";
    PayloadType[PayloadType["Sql"] = 2] = "Sql";
    PayloadType[PayloadType["Xml"] = 3] = "Xml";
    PayloadType[PayloadType["Text"] = 4] = "Text";
    PayloadType[PayloadType["Yaml"] = 5] = "Yaml";
})(PayloadType || (PayloadType = {}));
