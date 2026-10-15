// Microsoft.Extensions.Logging's names, as Watch uses. The old Serilog names stay as aliases until the next
// major version; each comes before its replacement so the reverse mapping (Level[0]) gives the new name.
export enum Level {
  /** @deprecated Use Level.Trace. */
  Verbose = 0,
  Trace = 0,
  Debug = 1,
  Information = 2,
  Warning = 3,
  Error = 4,
  /** @deprecated Use Level.Critical. */
  Fatal = 5,
  Critical = 5,
}

export enum PayloadType {
  None = 0,
  Json = 1,
  Sql = 2,
  Xml = 3,
  Text = 4,
  Yaml = 5,
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
