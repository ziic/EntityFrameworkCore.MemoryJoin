﻿using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntityFrameworkCore.MemoryJoin
{
    [Table("__stub_query_data", Schema = "__stub")]
    public class QueryModelClass
    {
        [Column("id"), Key(), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }


        [Column("long1")]
        public long? Long1 { get; set; }

        [Column("long2")]
        public long? Long2 { get; set; }

        [Column("long3")]
        public long? Long3 { get; set; }

        [Column("double1")]
        public Double? Double1 { get; set; }

        [Column("double2")]
        public Double? Double2 { get; set; }

        [Column("double3")]
        public Double? Double3 { get; set; }

        [Column("string1")]
        public string String1 { get; set; }

        [Column("string2")]
        public string String2 { get; set; }

        [Column("string3")]
        public string String3 { get; set; }

        [Column("date1")]
        public DateTime? Date1 { get; set; }

        [Column("date2")]
        public DateTime? Date2 { get; set; }

        [Column("date3")]
        public DateTime? Date3 { get; set; }

        [Column("guid1")]
        public Guid? Guid1 { get; set; }

        [Column("guid2")]
        public Guid? Guid2 { get; set; }

        [Column("guid3")]
        public Guid? Guid3 { get; set; }

    }
}
