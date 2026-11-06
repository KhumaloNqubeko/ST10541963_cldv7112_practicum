using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriStream.Migrations
{
    /// <inheritdoc />
    public partial class IrrigationWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IrrigationDevices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FarmId = table.Column<int>(type: "int", nullable: false),
                    FieldId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SensorSourceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OperationalStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LastCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastAction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    LastProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastOutcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IrrigationDevices", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_IrrigationDevices_Farms_FarmId",
                        column: x => x.FarmId,
                        principalTable: "Farms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IrrigationCommands",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FarmId = table.Column<int>(type: "int", nullable: false),
                    FieldId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CommandHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IrrigationCommands", x => x.CommandId);
                    table.ForeignKey(
                        name: "FK_IrrigationCommands_IrrigationDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "IrrigationDevices",
                        principalColumn: "DeviceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IrrigationCommands_DeviceId_ProcessedAt",
                table: "IrrigationCommands",
                columns: new[] { "DeviceId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IrrigationDevices_FarmId_FieldId",
                table: "IrrigationDevices",
                columns: new[] { "FarmId", "FieldId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IrrigationCommands");

            migrationBuilder.DropTable(
                name: "IrrigationDevices");
        }
    }
}
