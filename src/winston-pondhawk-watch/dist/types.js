export var Level;
(function (Level) {
    Level[Level["Verbose"] = 0] = "Verbose";
    Level[Level["Debug"] = 1] = "Debug";
    Level[Level["Information"] = 2] = "Information";
    Level[Level["Warning"] = 3] = "Warning";
    Level[Level["Error"] = 4] = "Error";
    Level[Level["Fatal"] = 5] = "Fatal";
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
