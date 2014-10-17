﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Examine.Web.Demo.Models
{
    public class TestModel
    {
        [Key]
        public int MyId { get; set; }
        public string Column1 { get; set; }
        public string Column2 { get; set; }
        public string Column3 { get; set; }
        public string Column4 { get; set; }        
        [Column(TypeName = "ntext")]
        [MaxLength]
        public string Column5 { get; set; }
        public string Column6 { get; set; }

    }
}