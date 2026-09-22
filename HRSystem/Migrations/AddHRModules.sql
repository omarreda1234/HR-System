-- ============================================================
-- HR System: AddHRModules Migration
-- Run this script directly on your SQL Server HR database
-- ============================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1. LeaveTypes
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveTypes')
BEGIN
    CREATE TABLE LeaveTypes (
        Id                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Name                NVARCHAR(50)  NOT NULL,
        NameAr              NVARCHAR(50)  NULL,
        DefaultDaysPerYear  INT           NOT NULL DEFAULT 0,
        RequiresApproval    BIT           NOT NULL DEFAULT 1,
        IsPaid              BIT           NOT NULL DEFAULT 1,
        ColorClass          NVARCHAR(30)  NOT NULL DEFAULT N'primary'
    );

    -- Seed default leave types
    INSERT INTO LeaveTypes (Name, NameAr, DefaultDaysPerYear, IsPaid, ColorClass) VALUES
    (N'Annual',   N'سنوية',       21, 1, N'primary'),
    (N'Casual',   N'عارضة',        6, 1, N'warning'),
    (N'Sick',     N'مرضية',        7, 1, N'danger'),
    (N'Unpaid',   N'بدون أجر',     0, 0, N'secondary');
END
GO

-- 2. LeaveBalances
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveBalances')
BEGIN
    CREATE TABLE LeaveBalances (
        Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo   VARCHAR(20)   NOT NULL,
        LeaveTypeId  INT           NOT NULL,
        [Year]       INT           NOT NULL,
        TotalDays    DECIMAL(8,2)  NOT NULL DEFAULT 0,
        UsedDays     DECIMAL(8,2)  NOT NULL DEFAULT 0,
        PendingDays  DECIMAL(8,2)  NOT NULL DEFAULT 0,

        CONSTRAINT UQ_LeaveBalance UNIQUE (EmployeeNo, LeaveTypeId, [Year]),
        CONSTRAINT FK_LeaveBalance_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE CASCADE,
        CONSTRAINT FK_LeaveBalance_LeaveType FOREIGN KEY (LeaveTypeId)
            REFERENCES LeaveTypes(Id)
    );
END
GO

-- 3. LeaveRequests
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveRequests')
BEGIN
    CREATE TABLE LeaveRequests (
        Id                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo        VARCHAR(20)   NOT NULL,
        LeaveTypeId       INT           NOT NULL,
        StartDate         DATE          NOT NULL,
        EndDate           DATE          NOT NULL,
        TotalDays         INT           NOT NULL,
        Reason            NVARCHAR(500) NULL,
        Status            INT           NOT NULL DEFAULT 0,  -- 0=Pending,1=ManagerApproved,2=HRApproved,3=Rejected,4=Cancelled
        ManagerNo         VARCHAR(20)   NULL,
        ManagerActionDate DATETIME      NULL,
        ManagerNotes      NVARCHAR(500) NULL,
        HRUserId          NVARCHAR(450) NULL,
        HRActionDate      DATETIME      NULL,
        HRNotes           NVARCHAR(500) NULL,
        CreatedAt         DATETIME      NOT NULL DEFAULT GETDATE(),

        CONSTRAINT FK_LeaveRequest_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE NO ACTION,
        CONSTRAINT FK_LeaveRequest_LeaveType FOREIGN KEY (LeaveTypeId)
            REFERENCES LeaveTypes(Id)
    );

    CREATE INDEX IX_LeaveRequest_Employee ON LeaveRequests(EmployeeNo);
    CREATE INDEX IX_LeaveRequest_Status   ON LeaveRequests(Status);
END
GO

-- 4. EmployeeDocuments
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeDocuments')
BEGIN
    CREATE TABLE EmployeeDocuments (
        Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo   VARCHAR(20)    NOT NULL,
        DocumentType NVARCHAR(50)   NOT NULL,
        FileName     NVARCHAR(255)  NOT NULL,
        FilePath     NVARCHAR(500)  NOT NULL,
        UploadDate   DATETIME       NOT NULL DEFAULT GETDATE(),
        ExpiryDate   DATE           NULL,
        Notes        NVARCHAR(500)  NULL,

        CONSTRAINT FK_EmployeeDocument_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE CASCADE
    );

    CREATE INDEX IX_EmployeeDoc_Employee ON EmployeeDocuments(EmployeeNo);
END
GO

-- 5. SalaryStructures
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SalaryStructures')
BEGIN
    CREATE TABLE SalaryStructures (
        Id                   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo           VARCHAR(20)  NOT NULL,
        BaseSalary           DECIMAL(18,2) NOT NULL DEFAULT 0,
        HousingAllowance     DECIMAL(18,2) NOT NULL DEFAULT 0,
        TransportAllowance   DECIMAL(18,2) NOT NULL DEFAULT 0,
        MedicalAllowance     DECIMAL(18,2) NOT NULL DEFAULT 0,
        OtherAllowances      DECIMAL(18,2) NOT NULL DEFAULT 0,
        TaxRate              DECIMAL(5,4)  NOT NULL DEFAULT 0,
        SocialInsuranceRate  DECIMAL(5,4)  NOT NULL DEFAULT 0.11,
        EffectiveFrom        DATE          NOT NULL DEFAULT CAST(GETDATE() AS DATE),
        IsActive             BIT           NOT NULL DEFAULT 1,
        Notes                NVARCHAR(500) NULL,

        CONSTRAINT FK_SalaryStructure_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX UQ_SalaryStructure_Active
        ON SalaryStructures(EmployeeNo)
        WHERE IsActive = 1;
END
GO

-- 6. Payslips
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Payslips')
BEGIN
    CREATE TABLE Payslips (
        Id                    INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo            VARCHAR(20)   NOT NULL,
        Month                 INT           NOT NULL,
        [Year]                INT           NOT NULL,
        BaseSalary            DECIMAL(18,2) NOT NULL DEFAULT 0,
        HousingAllowance      DECIMAL(18,2) NOT NULL DEFAULT 0,
        TransportAllowance    DECIMAL(18,2) NOT NULL DEFAULT 0,
        MedicalAllowance      DECIMAL(18,2) NOT NULL DEFAULT 0,
        OtherAllowances       DECIMAL(18,2) NOT NULL DEFAULT 0,
        GrossSalary           DECIMAL(18,2) NOT NULL DEFAULT 0,
        TaxAmount             DECIMAL(18,2) NOT NULL DEFAULT 0,
        SocialInsuranceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        LoanDeductionAmount   DECIMAL(18,2) NOT NULL DEFAULT 0,
        OtherDeductions       DECIMAL(18,2) NOT NULL DEFAULT 0,
        TotalDeductions       DECIMAL(18,2) NOT NULL DEFAULT 0,
        NetSalary             DECIMAL(18,2) NOT NULL DEFAULT 0,
        PdfPath               NVARCHAR(500) NULL,
        GeneratedAt           DATETIME      NOT NULL DEFAULT GETDATE(),
        IsPublished           BIT           NOT NULL DEFAULT 0,

        CONSTRAINT UQ_Payslip UNIQUE (EmployeeNo, Month, [Year]),
        CONSTRAINT FK_Payslip_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE CASCADE
    );

    CREATE INDEX IX_Payslip_MonthYear ON Payslips([Year], Month);
END
GO

-- 7. EmployeeLoans
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeLoans')
BEGIN
    CREATE TABLE EmployeeLoans (
        Id                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeNo          VARCHAR(20)   NOT NULL,
        TotalAmount         DECIMAL(18,2) NOT NULL,
        MonthlyInstallment  DECIMAL(18,2) NOT NULL,
        RemainingAmount     DECIMAL(18,2) NOT NULL,
        StartDate           DATE          NOT NULL DEFAULT CAST(GETDATE() AS DATE),
        Status              INT           NOT NULL DEFAULT 0,  -- 0=Active,1=Completed,2=Cancelled
        Notes               NVARCHAR(500) NULL,
        CreatedAt           DATETIME      NOT NULL DEFAULT GETDATE(),

        CONSTRAINT FK_EmployeeLoan_Employee FOREIGN KEY (EmployeeNo)
            REFERENCES Employees([No.]) ON DELETE CASCADE
    );
END
GO

-- 8. LoanDeductions
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoanDeductions')
BEGIN
    CREATE TABLE LoanDeductions (
        Id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        LoanId        INT          NOT NULL,
        PayslipId     INT          NOT NULL,
        Amount        DECIMAL(18,2) NOT NULL,
        DeductionDate DATETIME     NOT NULL DEFAULT GETDATE(),

        CONSTRAINT FK_LoanDeduction_Loan    FOREIGN KEY (LoanId)    REFERENCES EmployeeLoans(Id),
        CONSTRAINT FK_LoanDeduction_Payslip FOREIGN KEY (PayslipId) REFERENCES Payslips(Id)
    );
END
GO

PRINT N'✅ All HR module tables created successfully.';
