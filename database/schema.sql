-- Orthanc V1. Apply manually to an existing empty database (MySQL 9.7 LTS).
-- All DATETIME values are UTC. No automatic migrations or DDL at application startup.
-- Exact identifiers are normalized to lower-case ASCII by ingestion.
SET NAMES utf8mb4;

CREATE TABLE Customer (
    Id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    Name             VARCHAR(200)  NOT NULL,
    TimeZone         VARCHAR(100)  NOT NULL DEFAULT 'Etc/UTC',
    ReportRecipients VARCHAR(2000) NOT NULL DEFAULT '',
    ReportTime       TIME(6)       NOT NULL DEFAULT '00:05:00',
    Enabled          BOOLEAN       NOT NULL DEFAULT TRUE,
    HourlyAlerts     BOOLEAN       NOT NULL DEFAULT TRUE,
    CreatedZ         DATETIME(3)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ApiKey (
    Id         BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    CustomerId BIGINT       NOT NULL,
    KeyHash    VARCHAR(64)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Label      VARCHAR(200) NOT NULL,
    CreatedZ   DATETIME(3)  NOT NULL,
    RevokedZ   DATETIME(3)  NULL,
    UNIQUE KEY UX_ApiKey_Hash (KeyHash),
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE Job (
    Id                BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    CustomerId        BIGINT        NOT NULL,
    MachineName       VARCHAR(200)  NOT NULL,
    JobName           VARCHAR(200)  NOT NULL,
    Description       VARCHAR(2000) NULL,
    Enabled           BOOLEAN       NOT NULL DEFAULT TRUE,
    ExpectedDailyFrom DATE          NULL COMMENT 'NULL=unmonitored; otherwise first expected customer-local date',
    CreatedZ          DATETIME(3) NOT NULL,
    UNIQUE KEY UX_Job_Identity (CustomerId, MachineName, JobName),
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE JobRun (
    Id                    CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    JobId                 BIGINT      NOT NULL,
    StartedZ              DATETIME(3) NOT NULL,
    ReceivedZ             DATETIME(3) NOT NULL,
    FinishedZ             DATETIME(3) NULL,
    CompletedReceivedZ    DATETIME(3) NULL,
    DurationSeconds       DOUBLE      NULL,
    ExitCode              INT         NULL,
    Outcome               VARCHAR(20) NOT NULL DEFAULT 'Unknown',
    SourceIp              VARCHAR(45) NOT NULL,
    ProtocolVersion       VARCHAR(20) NOT NULL,
    Mode                  VARCHAR(20) NOT NULL,
    LateRegistration      BOOLEAN     NOT NULL DEFAULT FALSE,
    IgnoreExitCode        BOOLEAN     NOT NULL DEFAULT FALSE,
    TimedOut              BOOLEAN     NOT NULL DEFAULT FALSE,
    TerminationReason     VARCHAR(200) NULL,
    Truncated             BOOLEAN      NOT NULL DEFAULT FALSE,
    DroppedBytes          BIGINT       NOT NULL DEFAULT 0,
    LogText               LONGTEXT     NOT NULL,
    WarningText           LONGTEXT     NOT NULL,
    ErrorText             LONGTEXT     NOT NULL,
    WarningCount          INT          NOT NULL DEFAULT 0,
    ErrorCount            INT          NOT NULL DEFAULT 0,
    FatalCount            INT          NOT NULL DEFAULT 0,
    ClassificationLimited BOOLEAN      NOT NULL DEFAULT FALSE,
    CompletionHash        VARCHAR(64)  NULL,
    KEY IX_JobRun_Job_Start (JobId, StartedZ),
    KEY IX_JobRun_Completed (CompletedReceivedZ),
    KEY IX_JobRun_Outcome (Outcome),
    FOREIGN KEY (JobId) REFERENCES Job(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE LogPattern (
    Id         BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    CustomerId BIGINT        NULL,
    JobId      BIGINT        NULL,
    Regex      VARCHAR(2000) NOT NULL,
    Severity   VARCHAR(20)   NOT NULL,
    Priority   INT           NOT NULL DEFAULT 0,
    Enabled    BOOLEAN       NOT NULL DEFAULT TRUE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id),
    FOREIGN KEY (JobId)      REFERENCES Job(Id),
    CONSTRAINT CK_LogPattern_Severity CHECK (Severity IN ('Ignore','Warning','Error','Fatal'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ReportDelivery (
    Id           BIGINT      NOT NULL AUTO_INCREMENT PRIMARY KEY,
    CustomerId   BIGINT      NOT NULL,
    Kind         VARCHAR(20) NOT NULL,
    PeriodStartZ DATETIME(3) NOT NULL,
    PeriodEndZ   DATETIME(3) NOT NULL,
    SentZ        DATETIME(3) NULL,
    LastAttemptZ DATETIME(3) NULL,
    Attempts     INT         NOT NULL DEFAULT 0,
    UNIQUE KEY UX_ReportDelivery_Period (CustomerId, Kind, PeriodStartZ),
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
