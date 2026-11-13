using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recoletas.Booking.Infrastructure.EfModel.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientNhcUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicate_details text;
                BEGIN
                    SELECT string_agg(
                        format('%L (patient IDs: %s)', duplicate_nhc, patient_ids),
                        '; ')
                    INTO duplicate_details
                    FROM (
                        SELECT normalized_nhc AS duplicate_nhc,
                               string_agg("Id"::text, ', ' ORDER BY "Id") AS patient_ids
                        FROM (
                            SELECT "Id",
                                   NULLIF(regexp_replace(
                                       "Nhc",
                                       '^[[:space:]]+|[[:space:]]+$',
                                       '',
                                       'g'), '') AS normalized_nhc
                            FROM "Patients"
                            WHERE "Nhc" IS NOT NULL
                        ) normalized_patients
                        WHERE normalized_nhc IS NOT NULL
                        GROUP BY normalized_nhc
                        HAVING COUNT(*) > 1
                    ) duplicates;

                    IF duplicate_details IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Cannot enforce unique patient NHC values: duplicate values remain after whitespace trimming. Resolve these patient IDs explicitly; the migration does not delete or merge patient records. Duplicates: %',
                            duplicate_details;
                    END IF;

                    UPDATE "Patients"
                    SET "Nhc" = NULLIF(regexp_replace(
                        "Nhc",
                        '^[[:space:]]+|[[:space:]]+$',
                        '',
                        'g'), '')
                    WHERE "Nhc" IS DISTINCT FROM NULLIF(regexp_replace(
                        "Nhc",
                        '^[[:space:]]+|[[:space:]]+$',
                        '',
                        'g'), '');
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Nhc",
                table: "Patients",
                column: "Nhc",
                unique: true,
                filter: "\"Nhc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Patients_Nhc",
                table: "Patients");
        }
    }
}
