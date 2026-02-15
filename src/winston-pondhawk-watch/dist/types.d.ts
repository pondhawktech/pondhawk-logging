export declare enum Level {
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}
export declare enum PayloadType {
    None = 0,
    Json = 1,
    Sql = 2,
    Xml = 3,
    Text = 4,
    Yaml = 5
}
export interface LogEvent {
    Category: string;
    CorrelationId: string;
    Title: string;
    Tenant: string;
    Subject: string;
    Tag: string;
    Level: number;
    Color: number;
    Nesting: number;
    Occurred: string;
    Type: number;
    Payload: string | null;
    ErrorType: string | null;
}
export interface LogEventBatch {
    Uid: string;
    Domain: string;
    Events: LogEvent[];
}
