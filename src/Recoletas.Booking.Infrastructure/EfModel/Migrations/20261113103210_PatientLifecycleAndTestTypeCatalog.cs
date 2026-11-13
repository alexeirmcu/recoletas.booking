using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recoletas.Booking.Infrastructure.EfModel.Migrations
{
    /// <inheritdoc />
    public partial class PatientLifecycleAndTestTypeCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Patients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TestTypes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestTypes", x => x.Code);
                });

            migrationBuilder.InsertData(
                table: "TestTypes",
                columns: new[] { "Code" },
                values: new object[,] { { "BloodTest" }, { "Ultrasound" }, { "MRI" } });

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    unsupported_codes text;
                BEGIN
                    SELECT string_agg(code, ', ')
                    INTO unsupported_codes
                    FROM (
                        SELECT DISTINCT "TestType" AS code FROM "Slots"
                        UNION
                        SELECT DISTINCT "TestType" AS code FROM "WaitlistEntries"
                    ) existing_codes
                    WHERE code NOT IN ('BloodTest', 'Ultrasound', 'MRI')
                      AND code !~ '^BloodTest-[0-9a-fA-F]{32}$';

                    IF unsupported_codes IS NOT NULL THEN
                        RAISE EXCEPTION 'Cannot add the closed TestTypes catalog; unsupported persisted test types require an explicit mapping: %', unsupported_codes;
                    END IF;

                    -- Older integration tests generated synthetic BloodTest-{guid} values.
                    -- Retain every row while mapping that known fixture format to its canonical code.
                    UPDATE "Slots"
                    SET "TestType" = 'BloodTest'
                    WHERE "TestType" ~ '^BloodTest-[0-9a-fA-F]{32}$';

                    UPDATE "WaitlistEntries"
                    SET "TestType" = 'BloodTest'
                    WHERE "TestType" ~ '^BloodTest-[0-9a-fA-F]{32}$';

                    IF EXISTS (
                        SELECT 1 FROM "Slots" s
                        LEFT JOIN "Patients" p ON p."Id" = s."AssignedPatientId"
                        WHERE s."AssignedPatientId" IS NOT NULL AND p."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM "Reservations" r
                        LEFT JOIN "Patients" p ON p."Id" = r."PatientId"
                        WHERE p."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM "Reservations" r
                        LEFT JOIN "Slots" s ON s."Id" = r."SlotId"
                        WHERE s."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM "WaitlistEntries" w
                        LEFT JOIN "Patients" p ON p."Id" = w."PatientId"
                        WHERE p."Id" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot add restrictive booking foreign keys; orphaned patient/slot references exist. Repair the references explicitly; no booking data was deleted.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Slots_AssignedPatientId",
                table: "Slots",
                column: "AssignedPatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Slots_TestType",
                table: "Slots",
                column: "TestType");

            migrationBuilder.AddForeignKey(
                name: "FK_Reservations_Patients_PatientId",
                table: "Reservations",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reservations_Slots_SlotId",
                table: "Reservations",
                column: "SlotId",
                principalTable: "Slots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Slots_Patients_AssignedPatientId",
                table: "Slots",
                column: "AssignedPatientId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Slots_TestTypes_TestType",
                table: "Slots",
                column: "TestType",
                principalTable: "TestTypes",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WaitlistEntries_Patients_PatientId",
                table: "WaitlistEntries",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WaitlistEntries_TestTypes_TestType",
                table: "WaitlistEntries",
                column: "TestType",
                principalTable: "TestTypes",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reservations_Patients_PatientId",
                table: "Reservations");

            migrationBuilder.DropForeignKey(
                name: "FK_Reservations_Slots_SlotId",
                table: "Reservations");

            migrationBuilder.DropForeignKey(
                name: "FK_Slots_Patients_AssignedPatientId",
                table: "Slots");

            migrationBuilder.DropForeignKey(
                name: "FK_Slots_TestTypes_TestType",
                table: "Slots");

            migrationBuilder.DropForeignKey(
                name: "FK_WaitlistEntries_Patients_PatientId",
                table: "WaitlistEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_WaitlistEntries_TestTypes_TestType",
                table: "WaitlistEntries");

            migrationBuilder.DropTable(
                name: "TestTypes");

            migrationBuilder.DropIndex(
                name: "IX_Slots_AssignedPatientId",
                table: "Slots");

            migrationBuilder.DropIndex(
                name: "IX_Slots_TestType",
                table: "Slots");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Patients");
        }
    }
}
