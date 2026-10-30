// EF 自动生成的迁移结构说明；与同名迁移配套，不手工修改里面的表结构。
using DeviceMonitor.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace DeviceMonitor.Migrations
{
    [DbContext(typeof(MonitorDbContext))]
    [Migration("20261016085148_InitialCreate")]
    partial class InitialCreate
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

            modelBuilder.Entity("DeviceMonitor.Model.AlarmRow", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<long>("DeviceId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("EndedAtUtc")
                        .HasColumnType("TEXT");

                    b.Property<string>("EventId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Kind")
                        .HasColumnType("INTEGER");

                    b.Property<string>("LastSeenAtUtc")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int?>("LimitValue")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Message")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Occurrences")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("PointId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PointName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("StartedAtUtc")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("State")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("Value")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("EventId")
                        .IsUnique();

                    b.HasIndex("StartedAtUtc", "Id")
                        .IsDescending()
                        .HasDatabaseName("IX_AlarmRecords_StartedAtUtc_Id");

                    b.HasIndex("DeviceId", "StartedAtUtc", "Id")
                        .IsDescending(false, true, true)
                        .HasDatabaseName("IX_AlarmRecords_DeviceId_StartedAtUtc_Id");

                    b.ToTable("AlarmRecords", null, t =>
                        {
                            t.HasCheckConstraint("CK_AlarmRecords_DeviceId", "DeviceId > 0");

                            t.HasCheckConstraint("CK_AlarmRecords_Kind", "Kind IN (1, 2, 3)");

                            t.HasCheckConstraint("CK_AlarmRecords_Occurrences", "Occurrences > 0");

                            t.HasCheckConstraint("CK_AlarmRecords_PointId", "PointId IS NULL OR PointId > 0");

                            t.HasCheckConstraint("CK_AlarmRecords_State", "State IN (1, 2, 3)");
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.DeviceRow", b =>
                {
                    b.Property<long>("Id")
                        .HasColumnType("INTEGER");

                    b.Property<int>("BaudRate")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER")
                        .HasDefaultValue(4800);

                    b.Property<int>("CommunicationMode")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER")
                        .HasDefaultValue(0);

                    b.Property<int>("ConnectTimeoutMilliseconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Host")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsEnabled")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER")
                        .HasDefaultValue(false);

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT")
                        .UseCollation("NOCASE");

                    b.Property<int>("Parity")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER")
                        .HasDefaultValue(0);

                    b.Property<int>("Port")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ReadTimeoutMilliseconds")
                        .HasColumnType("INTEGER");

                    b.Property<int>("SamplingIntervalMilliseconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("SerialPortName")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT")
                        .HasDefaultValue("")
                        .UseCollation("NOCASE");

                    b.Property<int>("StopBits")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER")
                        .HasDefaultValue(1);

                    b.Property<int>("UnitId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("WriteTimeoutMilliseconds")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.HasIndex("SerialPortName", "UnitId")
                        .IsUnique()
                        .HasDatabaseName("IX_Devices_RtuStation")
                        .HasFilter("CommunicationMode = 1");

                    b.ToTable("Devices", null, t =>
                        {
                            t.HasCheckConstraint("CK_Devices_BaudRate", "BaudRate > 0");

                            t.HasCheckConstraint("CK_Devices_CommunicationMode", "CommunicationMode IN (0, 1)");

                            t.HasCheckConstraint("CK_Devices_ConnectTimeout", "ConnectTimeoutMilliseconds > 0");

                            t.HasCheckConstraint("CK_Devices_IsEnabled", "IsEnabled IN (0, 1)");

                            t.HasCheckConstraint("CK_Devices_Parity", "Parity IN (0, 1, 2)");

                            t.HasCheckConstraint("CK_Devices_Port", "Port BETWEEN 1 AND 65535");

                            t.HasCheckConstraint("CK_Devices_ReadTimeout", "ReadTimeoutMilliseconds > 0");

                            t.HasCheckConstraint("CK_Devices_SamplingInterval", "SamplingIntervalMilliseconds > 0");

                            t.HasCheckConstraint("CK_Devices_StopBits", "StopBits IN (1, 2)");

                            t.HasCheckConstraint("CK_Devices_UnitId", "UnitId BETWEEN 0 AND 255");

                            t.HasCheckConstraint("CK_Devices_WriteTimeout", "WriteTimeoutMilliseconds > 0");
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.HistoryRow", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<long>("DeviceId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<long>("PointId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PointName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ReadAtUtc")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Value")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("DeviceId", "ReadAtUtc")
                        .HasDatabaseName("IX_HistoryRecords_DeviceId_ReadAtUtc");

                    b.HasIndex("ReadAtUtc", "Id")
                        .IsDescending()
                        .HasDatabaseName("IX_HistoryRecords_ReadAtUtc_Id");

                    b.ToTable("HistoryRecords", null, t =>
                        {
                            t.HasCheckConstraint("CK_HistoryRecords_DeviceId", "DeviceId > 0");

                            t.HasCheckConstraint("CK_HistoryRecords_PointId", "PointId > 0");
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.PointRow", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("Address")
                        .HasColumnType("INTEGER");

                    b.Property<int>("DataType")
                        .HasColumnType("INTEGER");

                    b.Property<long>("DeviceId")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("LowerLimit")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT")
                        .UseCollation("NOCASE");

                    b.Property<int>("RegisterType")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("UpperLimit")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("DeviceId", "Name")
                        .IsUnique();

                    b.ToTable("DevicePoints", null, t =>
                        {
                            t.HasCheckConstraint("CK_DevicePoints_Address", "Address BETWEEN 0 AND 65535");

                            t.HasCheckConstraint("CK_DevicePoints_DataType", "DataType IN (0, 1)");

                            t.HasCheckConstraint("CK_DevicePoints_RegisterType", "RegisterType IN (3, 4)");
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.SqliteSequenceRow", b =>
                {
                    b.Property<string>("Name")
                        .HasColumnType("TEXT")
                        .HasColumnName("name");

                    b.Property<long>("Sequence")
                        .HasColumnType("INTEGER")
                        .HasColumnName("seq");

                    b.HasKey("Name");

                    b.ToTable("sqlite_sequence", null, t =>
                        {
                            t.ExcludeFromMigrations();
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.UserRow", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<byte[]>("PasswordHash")
                        .IsRequired()
                        .HasColumnType("BLOB");

                    b.Property<int>("PasswordIterations")
                        .HasColumnType("INTEGER");

                    b.Property<byte[]>("PasswordSalt")
                        .IsRequired()
                        .HasColumnType("BLOB");

                    b.Property<int>("Role")
                        .HasColumnType("INTEGER");

                    b.Property<string>("UserName")
                        .IsRequired()
                        .HasColumnType("TEXT")
                        .UseCollation("NOCASE");

                    b.HasKey("Id");

                    b.HasIndex("UserName")
                        .IsUnique();

                    b.ToTable("Users", null, t =>
                        {
                            t.HasCheckConstraint("CK_Users_IsEnabled", "IsEnabled IN (0, 1)");

                            t.HasCheckConstraint("CK_Users_PasswordIterations", "PasswordIterations > 0");

                            t.HasCheckConstraint("CK_Users_Role", "Role IN (0, 1)");
                        });
                });

            modelBuilder.Entity("DeviceMonitor.Model.PointRow", b =>
                {
                    b.HasOne("DeviceMonitor.Model.DeviceRow", null)
                        .WithMany()
                        .HasForeignKey("DeviceId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
